namespace LibrusNotifier;

/// <summary>Argumenty wiersza poleceń.</summary>
public sealed record CommandLineOptions(bool RunOnce, string? Directory, bool ShowHelp)
{
    public const string Usage = """
        Użycie: LibrusNotifier [--run-once] [--katalog <ścieżka>]

          --run-once            jeden cykl sprawdzeń (ping i pełny przegląd list), potem koniec
          --katalog <ścieżka>   katalog aplikacji z plikiem librus_config.txt i katalogiem data
                                (domyślnie katalog pliku wykonywalnego)
          --help                ta pomoc
        """;

    /// <exception cref="ArgumentException">Niepoprawne argumenty.</exception>
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var runOnce = false;
        var help = false;
        string? directory = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--run-once":
                    if (runOnce) throw new ArgumentException("Argument --run-once podano więcej niż raz.");
                    runOnce = true;
                    break;
                case "--katalog":
                    if (directory is not null) throw new ArgumentException("Argument --katalog podano więcej niż raz.");
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Po --katalog należy podać ścieżkę katalogu.");
                    directory = args[++i];
                    break;
                case "--help" or "-h" or "/?":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Nieznany argument: {args[i]}");
            }
        }
        return new CommandLineOptions(runOnce, directory, help);
    }
}
