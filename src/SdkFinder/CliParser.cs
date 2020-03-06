using System;
using System.Linq;
using System.Text;

namespace SdkFinder
{
    internal static class CliParser
    {
        private static CliOption[] GetCliOptions() => new[]
        {
            new CliOption("outdir", 1, (o, a) => o.OutDir = a[0]),
            new CliOption("intype", 't', 1, (o, a) => o.InFileType = ParseFileType(a[0])),
            new CliOption("dev", 'd', 0, (o, a) => o.UseDevKeys = true),
            new CliOption("enablehash", 'h', 0, (o, a) => o.EnableHash = true),
            new CliOption("keyset", 'k', 1, (o, a) => o.Keyfile = a[0]),
            new CliOption("titlekeys", 1, (o, a) => o.TitleKeyFile = a[0]),
            new CliOption("consolekeys", 1, (o, a) => o.ConsoleKeyFile = a[0]),
            new CliOption("accesslog", 1, (o, a) => o.AccessLog = a[0]),
            new CliOption("resultlog", 1, (o, a) => o.ResultLog = a[0]),
            new CliOption("sdseed", 1, (o, a) => o.SdSeed = a[0])
        };

        public static Options Parse(string[] args)
        {
            var options = new Options();
            bool inputSpecified = false;

            CliOption[] cliOptions = GetCliOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg;

                if (args[i].Length == 2 && (args[i][0] == '-' || args[i][0] == '/'))
                {
                    arg = args[i][1].ToString().ToLower();
                }
                else if (args[i].Length > 2 && args[i].Substring(0, 2) == "--")
                {
                    arg = args[i].Substring(2).ToLower();
                }
                else
                {
                    if (inputSpecified)
                    {
                        PrintWithUsage($"Unable to parse option {args[i]}");
                        return null;
                    }

                    options.InFile = args[i];
                    inputSpecified = true;
                    continue;
                }

                CliOption option = cliOptions.FirstOrDefault(x => x.Long == arg || x.Short == arg);
                if (option == null)
                {
                    PrintWithUsage($"Unknown option {args[i]}");
                    return null;
                }

                if (i + option.ArgsNeeded >= args.Length)
                {
                    PrintWithUsage($"Need {option.ArgsNeeded} parameter{(option.ArgsNeeded == 1 ? "" : "s")} after {args[i]}");
                    return null;
                }

                var optionArgs = new string[option.ArgsNeeded];
                Array.Copy(args, i + 1, optionArgs, 0, option.ArgsNeeded);

                option.Assigner(options, optionArgs);
                i += option.ArgsNeeded;
            }

            if (!inputSpecified)
            {
                PrintWithUsage("Input file must be specified");
                return null;
            }

            return options;
        }

        private static FileType ParseFileType(string input)
        {
            switch (input.ToLower())
            {
                case "searchsdk": return FileType.SearchSdk;
            }

            PrintWithUsage("Specified type is invalid.");

            return default;
        }

        private static void PrintWithUsage(string toPrint)
        {
            Console.WriteLine(toPrint);
            Console.WriteLine(GetUsage());
            // PrintUsage();
        }

        private static string GetUsage()
        {
            var sb = new StringBuilder();

            sb.AppendLine("Usage: CliParser [options...] <input_content_path>");
            sb.AppendLine("Options:");
            sb.AppendLine("  --outdir <dir>       Specify sdk output path.");
            sb.AppendLine("  -h, --enablehash     Enable hash checks when reading the input file.");
            sb.AppendLine("  -d, --dev            Decrypt with development keys instead of retail.");
            sb.AppendLine("  -k, --keyset         Load keys from an external file.");
            sb.AppendLine("  --titlekeys <file>   Load title keys from an external file.");
            sb.AppendLine("  --accesslog <file>   Specify the access log file path.");


            return sb.ToString();
        }

        private class CliOption
        {
            public CliOption(string longName, char shortName, int argsNeeded, Action<Options, string[]> assigner)
            {
                Long = longName;
                Short = shortName.ToString();
                ArgsNeeded = argsNeeded;
                Assigner = assigner;
            }

            public CliOption(string longName, int argsNeeded, Action<Options, string[]> assigner)
            {
                Long = longName;
                ArgsNeeded = argsNeeded;
                Assigner = assigner;
            }

            public string Long { get; }
            public string Short { get; }
            public int ArgsNeeded { get; }
            public Action<Options, string[]> Assigner { get; }
        }
    }
}
