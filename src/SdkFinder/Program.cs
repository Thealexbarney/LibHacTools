using System;
using System.IO;
using System.Text;
using LibHac;
using LibHac.Fs;

namespace SdkFinder
{
    public static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (Run(args)) return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\nERROR: {ex.Message}\n");

                Console.Error.WriteLine("Additional information:");
                Console.Error.WriteLine(ex.GetType().FullName);
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 1;
        }

        private static bool Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var ctx = new Context();
            ctx.Options = CliParser.Parse(args);
            if (ctx.Options == null) return false;

            StreamWriter logWriter = null;
            ResultLogger resultLogger = null;

            try
            {
                using (var logger = new ProgressBar())
                {
                    ctx.Logger = logger;
                    ctx.Horizon = new Horizon(new StopWatchTimeSpanGenerator());

                    if (ctx.Options.AccessLog != null)
                    {
                        logWriter = new StreamWriter(ctx.Options.AccessLog);
                        var accessLog = new TextWriterAccessLog(logWriter);

                        ctx.Horizon.Fs.SetAccessLogTarget(AccessLogTarget.All);
                        ctx.Horizon.Fs.SetGlobalAccessLogMode(GlobalAccessLogMode.Log);

                        ctx.Horizon.Fs.SetAccessLogObject(accessLog);
                    }

                    if (ctx.Options.ResultLog != null)
                    {
                        resultLogger = new ResultLogger(new StreamWriter(ctx.Options.ResultLog),
                            printStackTrace: true, printSourceInfo: true, combineRepeats: true);

                        Result.SetLogger(resultLogger);
                    }

                    OpenKeyset(ctx);

                    RunTask(ctx);
                }
            }
            finally
            {
                logWriter?.Dispose();

                if (resultLogger != null)
                {
                    Result.SetLogger(null);
                    resultLogger.Dispose();
                }
            }

            return true;
        }

        private static void RunTask(Context ctx)
        {
            switch (ctx.Options.InFileType)
            {
                case FileType.SearchSdk:
                    new ProcessSearchSdk(ctx).Process();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void OpenKeyset(Context ctx)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string homeKeyFile = Path.Combine(home, ".switch", "prod.keys");
            string homeDevKeyFile = Path.Combine(home, ".switch", "dev.keys");
            string homeTitleKeyFile = Path.Combine(home, ".switch", "title.keys");
            string homeConsoleKeyFile = Path.Combine(home, ".switch", "console.keys");
            string keyFile = ctx.Options.Keyfile;
            string titleKeyFile = ctx.Options.TitleKeyFile;
            string consoleKeyFile = ctx.Options.ConsoleKeyFile;

            if (keyFile == null && File.Exists(homeKeyFile))
            {
                keyFile = homeKeyFile;
            }

            if (titleKeyFile == null && File.Exists(homeTitleKeyFile))
            {
                titleKeyFile = homeTitleKeyFile;
            }

            if (consoleKeyFile == null && File.Exists(homeConsoleKeyFile))
            {
                consoleKeyFile = homeConsoleKeyFile;
            }

            ctx.Keyset = ExternalKeyReader.ReadKeyFile(keyFile, titleKeyFile, consoleKeyFile, ctx.Logger);
            if (ctx.Options.SdSeed != null)
            {
                ctx.Keyset.SetSdSeed(ctx.Options.SdSeed.ToBytes());
            }

            if (File.Exists(homeDevKeyFile))
            {
                ctx.DevKeyset = ExternalKeyReader.ReadKeyFile(homeDevKeyFile, titleKeyFile, consoleKeyFile, ctx.Logger, true);
                if (ctx.Options.SdSeed != null)
                {
                    ctx.DevKeyset.SetSdSeed(ctx.Options.SdSeed.ToBytes());
                }
            }
        }
    }

    internal class ToolContext
    {
        public Options Options;
        public Keyset Keyset;
        public ProgressBar Logger;
    }
}
