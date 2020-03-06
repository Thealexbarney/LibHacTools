using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;
using LibHac.FsSystem.Detail;
using LibHac.FsSystem.NcaUtils;
using LibHac.Spl;

namespace SdkFinder
{
    internal class ProcessSearchSdk
    {
        private Context Context { get; }
        private Keyset Keyset { get; }
        private FileSystemClient FsClient { get; }
        private HashSet<Buffer32> ProcessedBuildIds { get; } = new HashSet<Buffer32>();
        private byte[] Buffer { get; } = new byte[1024 * 1024 * 10];

        public ProcessSearchSdk(Context ctx)
        {
            Context = ctx;
            Keyset = ctx.Keyset;
            FsClient = ctx.Horizon.Fs;
        }

        public void Process()
        {
            if (Context.Options.OutDir == null)
            {
                Context.Logger.LogMessage("Output directory must be specified.");
                return;
            }

            var localFs = new LocalFileSystem(Context.Options.InFile);
            var outFs = new LocalFileSystem(Context.Options.OutDir);

            FsClient.Register("search".ToU8Span(), localFs);
            FsClient.Register("out".ToU8Span(), outFs);

            FsClient.OpenDirectory(out DirectoryHandle rootDir, "search:/", OpenDirectoryMode.File);
            using (rootDir)
            {
                var entry = new DirectoryEntry();

                string fileName = string.Empty;

                while (true)
                {
                    try
                    {
                        FsClient.ReadDirectory(out long entriesRead, SpanHelpers.AsSpan(ref entry), rootDir);

                        if (entriesRead == 0)
                            break;

                        fileName = StringUtils.Utf8ZToString(entry.Name);
                        Context.Logger.LogMessage(fileName);

                        string extension = Path.GetExtension(fileName);

                        if (extension.ToLower() == ".xci")
                        {
                            ProcessXci($"search:/{fileName}");
                        }
                        else if (extension.ToLower() == ".nsp")
                        {
                            ProcessNsp($"search:/{fileName}");
                        }
                        else if (extension.ToLower() == ".nca")
                        {
                            ProcessNcaFile($"search:/{fileName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing {fileName}");
                        Console.WriteLine(ex);
                    }
                }
            }

            FsClient.Unmount("search");
            FsClient.Unmount("out");
        }

        private void ProcessXci(string xciPath)
        {
            FsClient.OpenFile(out FileHandle xciHandle, xciPath, OpenMode.Read).ThrowIfFailure();

            using (var xciStorage = new FileHandleStorage(xciHandle, true))
            {
                var xci = new Xci(Keyset, xciStorage);

                if (!xci.HasPartition(XciPartitionType.Secure))
                    return;

                IFileSystem secureFs = xci.OpenPartition(XciPartitionType.Secure);
                ProcessNcaFs(secureFs);
            }
        }

        private void ProcessNsp(string nspPath)
        {
            FsClient.OpenFile(out FileHandle nspHandle, nspPath, OpenMode.Read).ThrowIfFailure();

            using (var nspStorage = new FileHandleStorage(nspHandle, true))
            {
                var pfs = new PartitionFileSystemCore<StandardEntry>();
                pfs.Initialize(nspStorage).ThrowIfFailure();

                ProcessNcaFs(pfs);
            }
        }

        private void ProcessNcaFile(string ncaPath)
        {
            FsClient.OpenFile(out FileHandle ncaHandle, ncaPath, OpenMode.Read).ThrowIfFailure();

            using (var ncaStorage = new FileHandleStorage(ncaHandle, true))
            {
                var nca = new Nca(Keyset, ncaStorage);

                ProcessNca(nca);
            }
        }

        private void ProcessNcaFs(IFileSystem ncaFs)
        {
            ImportTickets(ncaFs);

            foreach (DirectoryEntryEx fileEntry in ncaFs.EnumerateEntries("/", "*.nca"))
            {
                Result rc = ncaFs.OpenFile(out IFile ncaFile, fileEntry.FullPath, OpenMode.Read);
                if (rc.IsFailure()) continue;

                var nca = new Nca(Keyset, ncaFile.AsStorage());

                ProcessNca(nca);
            }
        }

        private void ProcessNca(Nca nca)
        {
            if (nca.CanOpenSection(NcaSectionType.Code))
            {
                IFileSystem codeFs = nca.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);

                Result rc = codeFs.OpenFile(out IFile sdkFile, "/sdk", OpenMode.Read);
                if (rc.IsSuccess())
                {
                    ProcessSdk(sdkFile);
                }
            }

            if (nca.CanOpenSection(NcaSectionType.Data))
            {
                int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, nca.Header.ContentType);

                if (!nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                {
                    IFileSystem dataFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);

                    foreach (DirectoryEntryEx fileEntry in dataFs.EnumerateEntries("/", "sdk",
                        SearchOptions.RecurseSubdirectories))
                    {
                        Result rc = dataFs.OpenFile(out IFile sdkFile, fileEntry.FullPath, OpenMode.Read);
                        if (rc.IsSuccess())
                        {
                            ProcessSdk(sdkFile);
                        }
                    }
                }
            }
        }

        private void ProcessSdk(IFile sdkFile)
        {
            var nso = new Nso(sdkFile.AsStorage());
            byte[] buildId = nso.BuildId;

            if (!ProcessedBuildIds.Add(Unsafe.As<byte, Buffer32>(ref buildId[0])))
                return;

            string fileName;

            if (TryGetVersion(nso, out string version))
            {
                fileName = $"out:/{version}_{buildId.ToHexString()}";
            }
            else
            {
                fileName = $"out:/{buildId.ToHexString()}";
            }

            Result rc = FsClient.GetEntryType(out _, fileName);
            if (rc.IsSuccess()) return;

            sdkFile.GetSize(out long sdkSize).ThrowIfFailure();
            Span<byte> sdkBytes = Buffer.AsSpan(0, (int)sdkSize);

            sdkFile.Read(out long bytesRead, 0, sdkBytes).ThrowIfFailure();
            if (bytesRead != sdkSize) return;

            FsClient.CreateFile(fileName, sdkSize).ThrowIfFailure();
            FsClient.OpenFile(out FileHandle outFile, fileName, OpenMode.Write).ThrowIfFailure();

            FsClient.WriteFile(outFile, 0, sdkBytes, WriteOption.Flush);
            FsClient.CloseFile(outFile);
        }

        private bool TryGetVersion(Nso nso, out string version)
        {
            byte[] rodata = nso.Sections[1].DecompressSection();
            byte[] search = Encoding.ASCII.GetBytes("NintendoSdk_nnSdk-");
            int searchLen = search.Length;

            for (int i = rodata.Length - searchLen; i > 0; i--)
            {
                Span<byte> data = rodata.AsSpan(i, searchLen);

                if (data.SequenceEqual(search))
                {
                    version = StringUtils.Utf8ZToString(rodata.AsSpan(i + searchLen));
                    return true;
                }
            }

            version = default;
            return false;
        }

        private void ImportTickets(IFileSystem fs)
        {
            foreach (DirectoryEntryEx ticketEntry in fs.EnumerateEntries("/", "*.tik"))
            {
                Result result = fs.OpenFile(out IFile ticketFile, ticketEntry.FullPath, OpenMode.Read);

                if (result.IsSuccess())
                {
                    var ticket = new Ticket(ticketFile.AsStream());

                    Keyset.ExternalKeySet.Add(new RightsId(ticket.RightsId), new AccessKey(ticket.GetTitleKey(Keyset)));
                }
            }
        }
    }
}
