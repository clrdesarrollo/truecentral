namespace TrueCentralVms.Core.Domain;

/// <summary>Formatos de PPU (placa patente única) chilena.</summary>
public enum ChileanPlateFormat
{
    /// <summary>No calza con ningún formato chileno.</summary>
    None,
    /// <summary>Formato antiguo: dos letras de una serie registrada y cuatro dígitos (AA·1000).</summary>
    Old,
    /// <summary>Formato nuevo de vehículos: cuatro letras y dos dígitos (BBBB·10).</summary>
    Car,
    /// <summary>Formato nuevo de motos: tres letras y dos dígitos (BBB·10).</summary>
    Motorcycle,
}

/// <summary>
/// Resultado de validar una patente. <paramref name="Plate"/> va normalizada
/// (mayúsculas, sin puntos, guiones ni espacios y sin el dígito verificador).
/// <paramref name="Problem"/> explica por qué no es válida; es null si lo es.
/// </summary>
public sealed record ChileanPlateCheck(string Plate, ChileanPlateFormat Format, char? CheckDigit, string? Problem)
{
    public bool IsValid => Format != ChileanPlateFormat.None && Problem is null;

    /// <summary>La patente con su dígito verificador ("BBBB10-K"), o tal cual si no es válida.</summary>
    public string Display => CheckDigit is { } dv ? $"{Plate}-{dv}" : Plate;

    public string FormatLabel => Format switch
    {
        ChileanPlateFormat.Old => "Formato antiguo (LL·NNNN)",
        ChileanPlateFormat.Car => "Formato nuevo (LLLL·NN)",
        ChileanPlateFormat.Motorcycle => "Moto, formato nuevo (LLL·NN)",
        _ => "No es formato chileno",
    };
}

/// <summary>
/// Validador de patentes chilenas según el «Instructivo para validación de
/// patentes v 2.0» del Servicio de Registro Civil e Identificación: formato,
/// serie y dígito verificador (módulo 11, el mismo del RUN).
///
/// Ojo: la placa física NO muestra el dígito verificador, así que una lectura
/// de cámara solo se puede validar por formato y serie; el dígito se calcula.
/// Si el texto trae el dígito tras un guion (p. ej. "BBBB01-9"), también se comprueba.
/// </summary>
public static class ChileanPlate
{
    /// <summary>
    /// Series del formato antiguo en el orden de su número (d7 d6 d5 del
    /// instructivo): AA = 1, BA = 2, … DW = 582. No es alfabético.
    /// </summary>
    private const string OldSeries =
        "AABACAEAFAGAHAABCBEBFBGBHBACBCECFCGCHCBDEDFDGDHDAEBECEEEFEGEHEAFBFCFEFFFGFHFAGBGCGEGFGHG" +
        "AHBHCHEHFHGHHHAJBJCJEJFJGJHJBKCKEKFKGKHKALBLCLELFLGLHLANBNCNENFNGNHNAPBPCPEPFPGPHPARBRCR" +
        "ERFRGRHRASBSCSESFSGSHSATBTCTETFTGTHTAUBUCUEUFUGUHUAVBVCVEVFVGVHVAXBXCXEXFXGXHXBYCYEYFYGY" +
        "HYAZBZCZEZFZGZDADBDDDEDFDGDHDIDJDKDLDNDPDRDSDTDUDVDXDYDZKAKBKCKDKEKFKGKHKJKKKLKNKPKRKSKT" +
        "KUKVKXKYKZLALBLCLDLELFLGLHLJLKLLLNLPLRLSLTLULVLXLYLZNANBNCNDNENFNGNHNJNKNLNNNPNRNSNTNUNV" +
        "NYNZPAPBPCPDPEPFPGPHPJPKPLPNPPPSPTPUPVPXPYPZNXRARBRCRDRERFRGRHRJRKRLRNRPRRRSRTRURVRXRYRZ" +
        "HZSASBSCSDSESFSGSHSJSKSLSNSPSRSSSTSUSVSXSYSZTATBTCTDTETFTGTHTJTKTLTNTPTRTSTTTUTVTXTYTZUA" +
        "UBUCUDUEUFUGUHUJUKULUNUPURUSUTUUUVUXUYUZVAVBVCVDVEVFVGVHVJVKVLVNVPVRVSVTVUVVVXVYVZXAXBXC" +
        "XDXEXFXGXHXJXKXLXMXNXPXQXRXSXTXUXVXXXYXZYAYBJAJBJCJDJEYCYDYEYFYGYHYJYKYLYNYPYRYSYTYUYVYX" +
        "YYYZZAZBZCZDZEZFZGZHZIZJZKZLJFJGJHZNZPZRZSZTZUZVZXZYZZJLJNJOJPJRJSWAWBWCWDWEWFWGWHWJWKWL" +
        "WNWPWRWSWTWUJJJKWVWWWXWYWZZWYWXWUWTWSWRWPWNWLWKWMZMYMXMVMUMTMSJTJUJVJWJXJYJZMAMBMCMDMEMF" +
        "MGMHMJMKMLMNMPMRIAIBICIDIEIFIGIHIJIKILINIOIPIRISITIUIVIWIXIYIZQAQBQCQDQEQFQGQHQJQKQLQNQO" +
        "QPQRQSQTQUQVQWQXQYQZOAOBOCODOEOFOGOHOJOKOLONOOOPOROSOTOUOVOWOXOYOZADAIAKAMAWAYBBBIBMBOBW" +
        "CCCDCICMCOCWDCDMDODW";

    private static readonly Dictionary<string, int> OldSeriesNumber = Enumerable.Range(0, OldSeries.Length / 2)
        .ToDictionary(i => OldSeries.Substring(i * 2, 2), i => i + 1);

    /// <summary>Letras de las series nuevas (autos y motos) y su dígito para el verificador.</summary>
    private static readonly Dictionary<char, int> NewLetterDigit = new()
    {
        ['B'] = 1, ['C'] = 2, ['D'] = 3, ['F'] = 4, ['G'] = 5, ['H'] = 6, ['J'] = 7, ['K'] = 8, ['L'] = 9,
        ['P'] = 0, ['R'] = 2, ['S'] = 3, ['T'] = 4, ['V'] = 5, ['W'] = 6, ['X'] = 7, ['Y'] = 8, ['Z'] = 9,
    };

    public static ChileanPlateCheck Validate(string? text)
    {
        // El verificador solo se reconoce tras un guion ("BBBB01-9"): una lectura de
        // cámara nunca lo trae, y "SXJ391" no debe tomarse por moto SXJ39 + DV 1.
        string raw = (text ?? "").Trim().ToUpperInvariant();
        char? given = null;
        int dash = raw.LastIndexOf('-');
        if (dash >= 0 && raw[(dash + 1)..].Trim() is [var dv] && dv is (>= '0' and <= '9') or 'K')
        {
            given = dv;
            raw = raw[..dash];
        }
        string plate = new string(raw.Where(char.IsLetterOrDigit).ToArray());

        // Patrón de letras (L) y dígitos (N).
        string shape = new string(plate.Select(c => char.IsAsciiDigit(c) ? 'N' : char.IsAsciiLetterUpper(c) ? 'L' : '?').ToArray());

        var result = shape switch
        {
            "LLNNNN" => ValidateOld(plate),
            "LLLLNN" => ValidateNew(plate, ChileanPlateFormat.Car, letters: 4),
            "LLLNN" => ValidateNew(plate, ChileanPlateFormat.Motorcycle, letters: 3),
            _ => new ChileanPlateCheck(plate, ChileanPlateFormat.None, null,
                "No calza con ningún formato chileno (LL·NNNN, LLLL·NN o LLL·NN)."),
        };

        if (given is { } g && result.CheckDigit is { } expected && g != expected)
            result = result with { Problem = $"El dígito verificador {g} no corresponde: debería ser {expected}." };
        return result;
    }

    /// <summary>Dígito verificador módulo 11 de <paramref name="number"/> (pesos 2…7 desde la derecha); 10 = 'K'.</summary>
    public static char CheckDigit(long number)
    {
        int sum = 0;
        for (int i = 0; number > 0; i++, number /= 10)
            sum += (int)(number % 10) * (2 + i % 6);
        int digit = 11 - sum % 11;
        return digit switch { 11 => '0', 10 => 'K', _ => (char)('0' + digit) };
    }

    private static ChileanPlateCheck ValidateOld(string plate)
    {
        if (!OldSeriesNumber.TryGetValue(plate[..2], out int series))
            return new(plate, ChileanPlateFormat.Old, null, $"La serie {plate[..2]} no existe en el formato antiguo.");
        long number = series * 10_000L + int.Parse(plate[2..]);
        return new(plate, ChileanPlateFormat.Old, CheckDigit(number), null);
    }

    private static ChileanPlateCheck ValidateNew(string plate, ChileanPlateFormat format, int letters)
    {
        int nd = 0;
        foreach (char letter in plate[..letters])
        {
            if (!NewLetterDigit.TryGetValue(letter, out int digit))
                return new(plate, format, null, $"La letra {letter} no se usa en las patentes nuevas.");
            nd = nd * 10 + digit;
        }
        // Autos: NV = ND·100 + nn. Motos: NV = ND·1000 + 0nn.
        long number = nd * (format == ChileanPlateFormat.Car ? 100L : 1000L) + int.Parse(plate[letters..]);
        return new(plate, format, CheckDigit(number), null);
    }
}
