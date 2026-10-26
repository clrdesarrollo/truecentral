// CLR TrueCentral VMS — panel: leer un bastón de rondas conectado a ESTE PC.
//
// Web Serial API (Chrome / Edge de escritorio, por HTTPS). Port a JS del driver
// del servidor (TrueCentralVms.Drivers.Patrol: PatrolFrame + JwmCaptorReader):
// bastón JWM Captor 4 por el puerto serie virtual del CP210x (VID 10C4,
// PID EA60) a 57600 8N1. El navegador solo transporta: sube los bloques CRUDOS
// y el servidor los decodifica con el mismo código que la base de descarga.
//
// Trama: D5 D5 | LEN_lo LEN_hi | CMD | DATA… | SUM_lo SUM_hi | 0D 0A
// LEN = bytes de CMD + DATA; SUM = suma de 16 bits de CMD + DATA. La respuesta
// lleva CMD | 0x80. LEN no siempre es cierto (la respuesta a 0x39 declara 1 y
// trae 2): la trama se cierra en el CR LF y la suma se calcula sobre lo recibido.
"use strict";

const PatrolFrame = {
  HEADER: 0xD5,
  OVERHEAD: 8,

  build(cmd, data = []) {
    const len = 1 + data.length;
    const f = new Uint8Array(len + this.OVERHEAD);
    f[0] = this.HEADER; f[1] = this.HEADER;
    f[2] = len & 0xFF; f[3] = (len >> 8) & 0xFF;
    f[4] = cmd;
    f.set(data, 5);
    const sum = this.sum(f.subarray(4, 4 + len));
    f[4 + len] = sum & 0xFF;
    f[5 + len] = (sum >> 8) & 0xFF;
    f[6 + len] = 0x0D;
    f[7 + len] = 0x0A;
    return f;
  },

  sum(bytes) {
    let s = 0;
    for (const b of bytes) s = (s + b) & 0xFFFF;
    return s;
  },

  isValid(f) {
    const n = f.length;
    if (n < this.OVERHEAD + 1 || f[0] !== this.HEADER || f[1] !== this.HEADER || f[n - 2] !== 0x0D || f[n - 1] !== 0x0A) return false;
    const sum = this.sum(f.subarray(4, n - 4));
    return f[n - 4] === (sum & 0xFF) && f[n - 3] === ((sum >> 8) & 0xFF);
  },

  /** Datos sin el comando, según el largo real. */
  payload(f) { return f.subarray(5, f.length - 4); },

  hex(f) { return Array.from(f, (b) => b.toString(16).padStart(2, "0")).join("").toUpperCase(); },
};

class PatrolReaderError extends Error {}
class PatrolCancelled extends Error {}

/** Conversación con el bastón por un SerialPort ya elegido por el usuario. */
class PatrolSerialReader {
  static BAUD = 57600;
  static WAKE_BYTES = 8192;   // 4096 no alcanza; 8192 (1,4 s) deja margen
  static CMD = { setTermNo: 0x01, getTermNo: 0x02, getRecords: 0x03, erase: 0x04, setDateTime: 0x05, identify: 0x0A, ack: 0x00, nak: 0xFF };
  static BLOCK_RESPONSE = 0x93;

  constructor(port) {
    this.port = port;
    this.reader = null;
    this.writer = null;
    this.pending = null;      // reader.read() en curso que un timeout dejó sin consumir
    this.cancelled = false;
  }

  async open() {
    await this.port.open({ baudRate: PatrolSerialReader.BAUD, dataBits: 8, stopBits: 1, parity: "none", bufferSize: 16384 });
    this.writer = this.port.writable.getWriter();
    this.reader = this.port.readable.getReader();
  }

  /** Suelta los bloqueos y cierra el puerto (siempre; ignora errores). */
  async close() {
    try { if (this.reader) await this.reader.cancel(); } catch { /* ya cerrado */ }
    try { this.reader?.releaseLock(); } catch { /* noop */ }
    try { this.writer?.releaseLock(); } catch { /* noop */ }
    this.reader = this.writer = this.pending = null;
    try { await this.port.close(); } catch { /* noop */ }
  }

  checkCancel() { if (this.cancelled) throw new PatrolCancelled("Lectura cancelada."); }

  sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

  async write(bytes) { await this.writer.write(bytes); }

  /** Un trozo de lo recibido, o null si no llegó nada en timeoutMs (la lectura sigue viva para la próxima vez). */
  async readChunk(timeoutMs) {
    if (!this.pending) this.pending = this.reader.read();
    let timer;
    const timeout = new Promise((resolve) => { timer = setTimeout(() => resolve(null), Math.max(0, timeoutMs)); });
    const result = await Promise.race([this.pending, timeout]);
    clearTimeout(timer);
    if (result === null) return null;
    this.pending = null;
    if (result.done) throw new PatrolReaderError("Se cerró el puerto serie.");
    return result.value;
  }

  /** Descarta lo que haya llegado (equivale a DiscardInBuffer). */
  async discardInput() {
    while (await this.readChunk(30)) { /* descartar */ }
  }

  /** Una trama: desde D5 D5 hasta el primer CR LF desde el largo declarado. */
  async readFrame(timeoutMs) {
    const deadline = performance.now() + timeoutMs;
    let buf = [];
    while (performance.now() < deadline) {
      this.checkCancel();
      const chunk = await this.readChunk(Math.min(200, deadline - performance.now()));
      if (!chunk || chunk.length === 0) continue;
      for (const b of chunk) buf.push(b);
      let start = 0;
      while (start < buf.length && buf[start] !== PatrolFrame.HEADER) start++;
      if (start) buf = buf.slice(start);
      if (buf.length < 4) continue;
      const declared = (buf[2] | (buf[3] << 8)) + PatrolFrame.OVERHEAD;
      if (buf.length < declared) continue;
      for (let end = declared; end <= Math.min(buf.length, declared + 4); end++)
        if (buf[end - 2] === 0x0D && buf[end - 1] === 0x0A) return Uint8Array.from(buf.slice(0, end));
      if (buf.length > declared + 4) return Uint8Array.from(buf.slice(0, declared));
    }
    return null;
  }

  async tryTransact(request, cmd, timeoutMs) {
    await this.discardInput();
    await this.write(request);
    const r = await this.readFrame(timeoutMs);
    return r && PatrolFrame.isValid(r) && r[4] === ((cmd | 0x80) & 0xFF) ? r : null;
  }

  async transact(request, cmd, timeoutMs, tries) {
    for (let i = 0; i < tries; i++) {
      this.checkCancel();
      const r = await this.tryTransact(request, cmd, timeoutMs);
      if (r) return r;
    }
    throw new PatrolReaderError(`El bastón no respondió al comando 0x${cmd.toString(16).padStart(2, "0").toUpperCase()}.`);
  }

  async ping() {
    return (await this.tryTransact(PatrolFrame.build(PatrolSerialReader.CMD.identify), PatrolSerialReader.CMD.identify, 400)) !== null;
  }

  async wakeUp() {
    await this.write(new Uint8Array(PatrolSerialReader.WAKE_BYTES).fill(0xFE));
    // write() puede volver antes de que salgan los bytes: se espera su tiempo de transmisión.
    await this.sleep(Math.ceil(PatrolSerialReader.WAKE_BYTES * 10 * 1000 / PatrolSerialReader.BAUD) + 100);
    await this.discardInput();
  }

  /** ¿Responde? Primero sin despertar (si ya está despierto contesta al tiro), si no, lo despierta. */
  async probe() {
    if (await this.ping()) return true;
    this.checkCancel();
    await this.wakeUp();
    for (let i = 0; i < 3; i++) {
      this.checkCancel();
      if (await this.tryTransact(PatrolFrame.build(PatrolSerialReader.CMD.identify), PatrolSerialReader.CMD.identify, 1000)) return true;
    }
    return false;
  }

  async getSerial() {
    const C = PatrolSerialReader.CMD;
    const p = PatrolFrame.payload(await this.transact(PatrolFrame.build(C.getTermNo), C.getTermNo, 1000, 3));
    if (p.length < 4) throw new PatrolReaderError("El bastón respondió un número incompleto.");
    return ((p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3]) >>> 0;
  }

  /** "SetModel(2, 1)" del fabricante (0x39 01): sin él no contesta la lectura de registros. */
  async prepareCommand() {
    await this.transact(PatrolFrame.build(0x39, [0x01]), 0x39, 1000, 3);
  }

  /** Pone el reloj: parts = { year, month, day, hour, minute, second } en hora local del servidor. */
  async setClock(parts) {
    const bcd = (v) => ((Math.floor(v / 10) << 4) | (v % 10)) & 0xFF;
    await this.prepareCommand();
    const C = PatrolSerialReader.CMD;
    const data = [bcd(parts.second), bcd(parts.minute), bcd(parts.hour), bcd(parts.day), 0, bcd(parts.month), bcd(parts.year % 100)];
    await this.transact(PatrolFrame.build(C.setDateTime, data), C.setDateTime, 1000, 3);
  }

  /** Lee toda la memoria (no borra). onProgress(block, blocks). */
  async readMemory(onProgress) {
    const C = PatrolSerialReader.CMD;
    await this.prepareCommand();
    const head = await this.transact(PatrolFrame.build(C.getRecords), C.getRecords, 6000, 3);
    const blocks = [];
    if ((head[2] | (head[3] << 8)) === 9) return { blocks, declared: 0 }; // misma condición del fabricante: vacío
    const p = PatrolFrame.payload(head);
    const declared = p.length >= 2 ? (p[0] | (p[1] << 8)) : 0;
    onProgress?.(0, declared);
    const ack = PatrolFrame.build(C.ack);
    const nak = PatrolFrame.build(C.nak);
    try {
      for (let i = 0; i < declared; i++) {
        this.checkCancel();
        let block = null;
        let request = ack;
        for (let attempt = 0; attempt < 3 && !block; attempt++) {
          await this.write(request);
          const b = await this.readFrame(2000);
          if (b && PatrolFrame.isValid(b) && b[4] === PatrolSerialReader.BLOCK_RESPONSE) block = b;
          request = nak;
        }
        if (!block) throw new PatrolReaderError(`No se pudo leer el bloque ${i + 1} de ${declared}.`);
        blocks.push(block);
        onProgress?.(i + 1, declared);
      }
    } finally {
      try { await this.write(ack); } catch { /* el puerto pudo cerrarse */ }
    }
    return { blocks, declared };
  }

  /** Vacía la memoria: el fabricante espera 4 s antes y hasta 40 s la confirmación (0x84). */
  async eraseMemory() {
    const C = PatrolSerialReader.CMD;
    await this.prepareCommand();
    await this.sleep(4000);
    await this.transact(PatrolFrame.build(C.erase), C.erase, 40000, 1);
  }
}

/** "2026-10-26T13:17:20.118-03:00" → partes y texto sin zona (la hora local del servidor, tal cual). */
function patrolParseServerLocal(text) {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})/.exec(text || "");
  if (!m) return null;
  const [year, month, day, hour, minute, second] = m.slice(1).map(Number);
  return { year, month, day, hour, minute, second, text: `${m[1]}-${m[2]}-${m[3]}T${m[4]}:${m[5]}:${m[6]}` };
}

// ---------------------------------------------------------------------------
// Ventana de lectura

function patrolSerialModal() {
  if (!("serial" in navigator)) {
    toast("Este navegador no permite el acceso al puerto serie: use Chrome o Edge de escritorio, por HTTPS.", true);
    return;
  }
  const canErase = Perms.can("patrol.erase");
  openModal(`
    <h3>Leer bastón desde este PC</h3>
    <div class="info-box">Conecte el bastón (o su base USB) a este equipo y presione <b>Conectar y leer</b>; el navegador
      le pedirá elegir el puerto (<i>CP210x</i>). Se lee la memoria, se pone en hora con la del servidor y las lecturas se
      guardan en la bitácora. Si la base de descarga de este PC está usando el puerto, deténgala antes.</div>
    ${canErase ? `<label class="checkbox-row"><input type="checkbox" id="ps-erase"> Vaciar el bastón después de guardar</label>` : ""}
    <div id="ps-status" class="pt-serial-status muted">Listo para conectar.</div>
    <div class="pt-progress hidden" id="ps-progress-wrap">
      <div class="pt-progress-bar"><span id="ps-progress-fill"></span></div>
      <span class="muted" id="ps-progress-text"></span>
    </div>
    <div id="ps-result"></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="ps-close">Cerrar</button>
      <button class="btn danger hidden" type="button" id="ps-cancel" title="Detiene la lectura; el bastón NO se vacía">Cancelar lectura</button>
      <button class="btn" type="button" id="ps-start"><i class="mdi mdi-usb" aria-hidden="true"></i> Conectar y leer</button>
    </div>`);

  let session = null;
  const status = (text, tone) => {
    const el = $("#ps-status");
    if (!el) return;
    el.textContent = text;
    el.className = `pt-serial-status ${tone === "error" ? "pt-error" : "muted"}`;
  };
  const progress = (done, total) => {
    $("#ps-progress-wrap")?.classList.remove("hidden");
    const pct = total ? Math.round((done / total) * 100) : 100;
    if ($("#ps-progress-fill")) $("#ps-progress-fill").style.width = `${pct}%`;
    if ($("#ps-progress-text")) $("#ps-progress-text").textContent = total ? `Bloque ${done} de ${total} (${pct} %)` : "Memoria vacía";
  };
  const busy = (on) => {
    $("#ps-start").disabled = on;
    $("#ps-close").disabled = on;
    $("#ps-cancel").classList.toggle("hidden", !on);
    if ($("#ps-erase")) $("#ps-erase").disabled = on;
  };

  $("#ps-close").addEventListener("click", closeModal);
  $("#ps-cancel").addEventListener("click", () => {
    if (!session) return;
    session.cancelled = true;
    status("Cancelando…");
  });

  $("#ps-start").addEventListener("click", async () => {
    $("#ps-result").innerHTML = "";
    let port;
    // requestPort tiene que llamarse directo desde el clic del usuario.
    try {
      port = await navigator.serial.requestPort({ filters: [{ usbVendorId: 0x10C4, usbProductId: 0xEA60 }] });
    } catch (err) {
      if (err?.name !== "NotFoundError") status(`No se pudo elegir el puerto: ${err?.message ?? err}`, "error");
      return;
    }
    const wantErase = !!$("#ps-erase")?.checked;
    session = new PatrolSerialReader(port);
    busy(true);
    try {
      await patrolSerialRun(session, wantErase, status, progress);
    } catch (err) {
      if (err instanceof PatrolCancelled) status("Lectura cancelada: no se guardó nada y el bastón no se vació.");
      else status(err?.error ?? err?.message ?? String(err), "error");
    } finally {
      await session.close();
      session = null;
      busy(false);
    }
  });
}

async function patrolSerialRun(link, wantErase, status, progress) {
  status("Abriendo el puerto…");
  try { await link.open(); }
  catch (err) {
    throw new PatrolReaderError(`No se pudo abrir el puerto (${err?.message ?? err}). ¿Lo está usando la base de descarga u otro programa?`);
  }

  status("Despertando el bastón…");
  if (!(await link.probe())) throw new PatrolReaderError("El bastón no responde. Revise que esté bien conectado y vuelva a intentarlo.");
  const serial = await link.getSerial();
  const serialText = String(serial).padStart(6, "0");
  status(`Bastón ${serialText}: poniendo en hora…`);

  // Hora local del SERVIDOR (no la de este PC): así todos los bastones quedan con la misma.
  let clockSetAt = null;
  try {
    const time = await Api.get("/api/patrol/server-time");
    const parts = patrolParseServerLocal(time.local);
    if (parts) {
      await link.setClock(parts);
      clockSetAt = parts.text;
    }
  } catch (err) {
    if (err instanceof PatrolCancelled) throw err;
    // Sin hora no se detiene la lectura: se sube igual y queda sin "puesta en hora".
  }

  status(`Bastón ${serialText}: leyendo la memoria…`);
  const { blocks, declared } = await link.readMemory(progress);
  progress(blocks.length, declared);

  status(`Bastón ${serialText}: guardando ${blocks.length} bloque(s) en el servidor…`);
  const result = await Api.post("/api/patrol/browser-upload", {
    serial, blocksHex: blocks.map((b) => PatrolFrame.hex(b)), declaredBlocks: declared, clockSetAt,
  });
  const lines = [`<div>${esc(result.message)}</div>`];
  if (!clockSetAt) lines.push(`<div class="muted">No se pudo poner en hora el bastón.</div>`);

  if (wantErase) {
    if (!result.eraseAllowed) {
      lines.push(`<div class="muted">No se vació: el bastón no está aprobado y habilitado, o sus roles no permiten vaciarlo.</div>`);
    } else if (confirm(`Las lecturas del bastón ${serialText} quedaron guardadas en el servidor. ¿Vaciar ahora su memoria?`)) {
      status(`Bastón ${serialText}: vaciando la memoria (puede tardar hasta 45 s)…`);
      let erased = false, error = null;
      try {
        // Pudo dormirse mientras se confirmaba.
        if (!(await link.ping()) && !(await link.probe())) throw new PatrolReaderError("El bastón no responde.");
        await link.eraseMemory();
        erased = true;
      } catch (err) {
        error = err?.message ?? String(err);
      }
      try { await Api.post("/api/patrol/browser-erase-report", { downloadId: result.downloadId, erased, error }); }
      catch (err) { lines.push(`<div class="pt-error">No se pudo informar el vaciado al servidor: ${esc(err.error)}</div>`); }
      lines.push(erased ? `<div>Memoria del bastón vaciada.</div>` : `<div class="pt-error">No se pudo vaciar: ${esc(error)}</div>`);
    } else {
      lines.push(`<div class="muted">No se vació la memoria.</div>`);
    }
  }
  status(`Bastón ${serialText}: lectura terminada.`);
  $("#ps-result").innerHTML = `<div class="info-box">${lines.join("")}</div>`;
}
