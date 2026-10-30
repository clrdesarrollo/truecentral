// Internet Explorer 11 y anteriores: el servidor ya responde un aviso; esto
// cubre la página que haya quedado guardada. ES3 a propósito. Va en un archivo
// aparte porque la política de contenido (CSP) no deja correr scripts en línea.
if (document.documentMode) { document.write('<p style="font-family:Segoe UI,Arial,sans-serif;margin:40px">Internet Explorer no es compatible con este panel. Ábralo con Microsoft Edge u otro navegador actual.</p>'); document.execCommand('Stop'); }
