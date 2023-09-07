namespace MyPoePatcher
{
    using System.IO.Compression;
    using LibGGPK2;
    using LibGGPK2.Records;

    internal class Program
    {
        private static int i = 0;
        private static GGPKContainer ggpkContainer;

        private static void 遍历(RecordTreeNode? node, string dir)
        {
            if (node == null)
            {
                return;
            }

            //Console.WriteLine(Path.Combine(dir, node.Name));
            if (node is IFileRecord f)
            {
                try
                {
                    var b = f.ReadFileContent(ggpkContainer.fileStream);
                    var p = Path.Combine(dir, node.Name);
                    Console.WriteLine(p);
                    //var b = f.ReadFileContent()
                    i++;
                    Directory.CreateDirectory(dir);
                    //try
                    //{
                    File.WriteAllBytes(p, b);
                    //}
                    //catch (Exception e)
                    //{
                    //}
                }
                catch (Exception e)
                {
                }


                //return;
            }


            if (node.Children == null)
            {
                return;
            }

            foreach (var recordTreeNode in node.Children)
            {
                遍历(recordTreeNode, Path.Combine(dir, node.Name));
            }
        }

        static void Main(string[] args)
        {
            //  var path = "D:\\WeGameApps\\流放之路\\Content.ggpk";
            var workDir = Environment.CurrentDirectory;
            var patcherPath = "D:\\Downloads\\[3.2200.8.1]_功能补丁+简改(v3)_V31.5.zip";
            var unpackPatcherPath = Path.Combine(workDir, "旧补丁\\ROOT");
            Console.WriteLine("Hello, World!");


            ZipFile.ExtractToDirectory(patcherPath, unpackPatcherPath, null, true);

            ggpkContainer = new GGPKContainer(Path.Combine(unpackPatcherPath, "Bundles2\\_.index.bin"), false, true);
            遍历(ggpkContainer.rootDirectory, Path.Combine(workDir, "旧补丁\\ROOT2"));
            Console.WriteLine(i);

            //var list = new List<KeyValuePair<IFileRecord, string>>();
            //var path = Path.Combine(workDir, "旧补丁\\ROOT2");
            //GGPKContainer.RecursiveFileList(ggpk.rootDirectory, path, list, true);
            //GGPKContainer.Export(list);

            //foreach (var keyValuePair in list)
            //{
            //    Console.WriteLine(keyValuePair.Value);
            //}

            //list.Sort((a, b) => BundleSortComparer.Instance.Compare(a.Key, b.Key));
            //var failFileCount = 0;
            //try
            //{
            //    GGPKContainer.Export(list);
            //}
            //catch (GGPKContainer.BundleMissingException bex)
            //{
            //    failFileCount = bex.failFiles;
            //}


            //foreach (var rootDirectoryChild in ggpk.rootDirectory.Children)
            //{
            //    if (rootDirectoryChild is FileRecord f)
            //    {
            //        f.ReadFileContent()
            //    }

            //    Console.WriteLine(rootDirectoryChild.Name);
            //}
            //var dat = new DatContainer("D:\\npcs.dat64", true);
            //var text = dat.ToCsv();
            //File.WriteAllText("D:\\npcs.csv", text, Encoding.UTF8);
        }
    }
}