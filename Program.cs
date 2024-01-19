using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.Text;
using RT.CommandLine;
using RT.Util;
using RT.Util.Consoles;
using RT.Util.ExtensionMethods;
using static GitDisAssemble.Program;

namespace GitDisAssemble;

static class Program
{
    static CmdLine Args;

    static int Main(string[] args)
    {
        Args = CommandLineParser.ParseOrWriteUsageToConsole<CmdLine>(args);
        if (Args == null)
            return -1;
        try
        {
            return Args.Execute();
        }
        catch (TellUserException e)
        {
            Console.WriteLine();
            ConsoleUtil.WriteLine("Error: ".Color(ConsoleColor.Red) + C(e.Message));
            return e.ExitCode;
        }
#if !DEBUG
        catch (Exception e)
        {
            Console.WriteLine();
            ConsoleUtil.WriteLine("Internal error: ".Color(ConsoleColor.Red) + e.Message);
            ConsoleUtil.WriteLine(e.GetType().FullName);
            ConsoleUtil.WriteLine(e.StackTrace);
            return -99;
        }
#endif
    }

    public static ConsoleColoredString C(string commandLineColoredRhoML)
    {
        // colors: refs=cyan, commit names=yellow, commit ids=green
        return CommandLineParser.Colorize(RhoML.Parse(commandLineColoredRhoML));
    }
}

[CommandLine]
abstract class CmdLine
{
    [Option("-ge", "--git-executable")]
    public string GitExe = @"C:\Apps\Git\cmd\git.exe";
    [Option("--auto-crlf")]
    [DocumentationLiteral("Enables line ending normalisation (by default GitDisAssemble preserves them as-is)")]
    public bool AutoCrLf = false;

    public abstract int Execute();

    protected string git(string repo, byte[] stdin, params string[] args)
    {
        var arglist = new List<string>();
        arglist.Add(GitExe);
        arglist.Add("-c");
        arglist.Add("core.autocrlf=" + (AutoCrLf ? "true" : "false"));
        arglist.AddRange(args);
        var runner = new CommandRunner();
        runner.WorkingDirectory = repo;
        runner.SetCommand(arglist);
        runner.CaptureEntireStdout = true;
        runner.CaptureEntireStderr = true;
        runner.Start(stdin);
        runner.EndedWaitHandle.WaitOne();
        if (runner.ExitCode != 0)
            throw new Exception($"Git command exited with status {runner.ExitCode}: " + runner.Command + "\r\n" + runner.EntireStderr.FromUtf8());
        var output = runner.EntireStdout.FromUtf8();
        if (output.EndsWith("\r\n"))
            throw new Exception("Unexpected \\r\\n");
        return output;
    }
}

[CommandName("disassemble", "d")]
[Documentation("Disassemble a git repository into files and directories describing each commit in full.")]
class CmdDisassemble : CmdLine
{
    [IsPositional, IsMandatory]
    [Documentation("Path to repository to disassemble.")]
    public string InputRepo = null;
    [IsPositional, IsMandatory]
    [Documentation("Path where the disassembled repository files are output.")]
    public string OutputPath = null;
    [IsPositional]
    [Documentation("When discovering commits to include, start at these commits or refs and include all parent commits recursively.")]
    public string[] AddRefs = null;
    [Option("-ah", "--add-heads")]
    [Documentation("When discovering commits to include, add all heads and include all parent commits recursively.")]
    public bool AddHeads = false;
    [Option("-at", "--add-tags")]
    [Documentation("When discovering commits to include, add all tags and include all parent commits recursively.")]
    public bool AddTags = false;
    [Option("-ac", "--add-children")]
    [Documentation("When discovering commits to include, also recursively include all child commits. You probably don't want this; instead make sure to include all the interesting heads and tags.")]
    public bool AddChildren = false;

    // customization todo: folder name template; whether to use author or commit time for folders; zulu times + timezone
    // todo: optionally don't disassemble trees, requiring reassembly into a repository that already has all the tree objects

    public override int Execute()
    {
        var allrefs = splitNewlineTerminated(git(InputRepo, null, "show-ref")).Select(r => (id: r.Split(' ')[0], rf: r.Split(' ')[1])).ToList();
        var ref2id = allrefs.ToDictionary(x => x.rf, x => x.id);
        var allheads = allrefs.Where(x => x.rf.StartsWith("refs/heads/")).ToList();
        var alltags = allrefs.Where(x => x.rf.StartsWith("refs/tags/")).ToList();
        ConsoleColoredString fmtRefList(IEnumerable<string> strs) => strs.Count() == 0 ? "" : (": " + strs.Take(4).Select(r => r.Color(ConsoleColor.Cyan)).JoinColoredString(", ") + (strs.Count() > 4 ? ", etc" : ""));
        ConsoleUtil.WriteLine($"Found {allheads.Count} heads" + fmtRefList(allheads.Select(x => x.rf)));
        ConsoleUtil.WriteLine($"Found {alltags.Count} tags" + fmtRefList(alltags.Select(x => x.rf)));
        var otherrefs = allrefs.Select(x => x.rf).Except(allheads.Select(x => x.rf)).Except(alltags.Select(x => x.rf)).ToList();
        if (otherrefs.Count > 0)
            ConsoleUtil.WriteLine($"Found {otherrefs.Count} other refs" + fmtRefList(otherrefs));

        var allobjects = splitNewlineTerminated(git(InputRepo, null, "cat-file", "--batch-check", "--batch-all-objects", "--unordered")).Select(r => r.Split(' ')).ToList();
        var commitIds = allobjects.Where(r => r[1] == "commit").Select(r => r[0]).ToList();
        Console.WriteLine($"Found {commitIds.Count} commit objects");

        Console.WriteLine($"Reading every commit...");
        var commits = commitIds.AsParallel().WithDegreeOfParallelism(10).Select(cid => Commit.ParseRawCommit(cid, git(InputRepo, null, "cat-file", "-p", cid))).ToList();
        var nodes = commits.ToDictionary(c => c.Id, c => new CommitNode { Commit = c });
        foreach (var node in nodes.Values)
        {
            node.Parents = node.Commit.Parents.Select(p => nodes[p]).ToList();
            foreach (var p in node.Parents)
                p.Children.Add(node);
        }

        var addRefsNodes = new List<CommitNode>();
        foreach (var addRef in AddRefs)
        {
            if (nodes.ContainsKey(addRef))
                addRefsNodes.Add(nodes[addRef]);
            else if (ref2id.ContainsKey(addRef))
                addRefsNodes.Add(nodes[ref2id[addRef]]);
            else
                throw new TellUserException(1, $"The value {{h}}{addRef}{{}} passed to {{field}}AddRefs{{}} is not a known commit or ref name. For refs, use full names (such as {{cyan}}refs/heads/main{{}}).");
        }

        var discovered = new HashSet<CommitNode>();
        void discoverAdd(CommitNode n)
        {
            if (!discovered.Add(n))
                return;
            foreach (var p in n.Parents)
                discoverAdd(p);
            if (AddChildren)
                foreach (var c in n.Children)
                    discoverAdd(c);
        }
        foreach (var n in addRefsNodes)
            discoverAdd(n);
        if (AddHeads)
            foreach (var x in allheads)
                discoverAdd(nodes[x.id]);
        if (AddTags)
            foreach (var x in alltags)
                discoverAdd(nodes[x.id]);

        string msgpreview(IEnumerable<string> msg)
        {
            var s = new string(string.Join(" ", msg).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '.').ToArray());
            s = s.Replace("....", "."); // lazy...
            s = s.Replace("...", ".");
            for (int i = 0; i < 10; i++)
                s = s.Replace("..", ".");
            return s.SubstringSafe(0, 20).Trim('.');
        }

        // assign unique names that will replace commit hashes
        foreach (var c in discovered)
            c.Commit.DirName = $"{c.Commit.AuthorTime:yyyy.MM.dd--HH.mm.sso<+HH.mm>}--{c.Commit.Id[..8]}--{msgpreview(c.Commit.Message)}";

        // write out refs - just those that point at a discovered commit
        Directory.CreateDirectory(OutputPath);
        foreach (var r in allrefs)
        {
            if (!nodes.ContainsKey(r.id) /* eg subrepos */ || !discovered.Contains(nodes[r.id]))
                continue;
            var path = Path.Combine(OutputPath, r.rf);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, nodes[r.id].Commit.DirName);
        }

        // write out commits
        Console.WriteLine($"Writing {discovered.Count} commits...");
        foreach (var c in discovered)
        {
            ConsoleUtil.WriteLine(C($"Writing {{yellow}}{c.Commit.DirName}{{}}"));
            var path = Path.Combine(OutputPath, c.Commit.DirName);
            Directory.CreateDirectory(path);
            git(InputRepo, null, "worktree", "add", Path.Combine(path, "temptree"), c.Commit.Id);
            Directory.Move(Path.Combine(path, "temptree"), Path.Combine(path, "tree"));
            File.Delete(Path.Combine(path, "tree", ".git"));
            git(InputRepo, null, "worktree", "prune");

            File.WriteAllText(Path.Combine(path, "message.txt"), c.Commit.Message.JoinString("\r\n"));
            for (int pi = 0; pi < c.Parents.Count; pi++)
                File.WriteAllText(Path.Combine(path, $"parent{pi}.txt"), c.Parents[pi].Commit.DirName);
            File.WriteAllText(Path.Combine(path, "author.txt"), c.Commit.Author);
            if (c.Commit.Committer != c.Commit.Author)
                File.WriteAllText(Path.Combine(path, "committer.txt"), c.Commit.Committer);
            if (c.Commit.CommitTime != c.Commit.AuthorTime)
                File.WriteAllText(Path.Combine(path, "commit-time.txt"), $"{c.Commit.CommitTime:yyyy.MM.dd--HH.mm.sso<+HH.mm>}");
        }

        return 0;
    }

    static string[] splitNewlineTerminated(string lines)
    {
        if (lines == "") return new string[0];
        if (!lines.EndsWith("\n"))
            throw new Exception("Expected a newline");
        return lines[..^1].Split('\n');
    }
}

[CommandName("assemble", "a")]
[Documentation("Assemble a git repository from files and directories describing each commit.")]
class CmdAssemble : CmdLine
{
    [IsPositional, IsMandatory]
    public string InputPath = null;
    [IsPositional, IsMandatory]
    [Documentation("Path to the output repository. If this directory does not exist, a blank new repository is created automatically.")]
    public string OutputRepo = null;

    public override int Execute()
    {
        // Find commits
        var nodes = new DirectoryInfo(InputPath).GetDirectories().Where(d => d.Name != "refs")
            .ToDictionary(d => d.Name, d => new CommitNode { Commit = new() { DirName = d.Name } });

        // Parse commits
        var timePattern = OffsetDateTimePattern.CreateWithInvariantCulture("yyyy.MM.dd--HH.mm.sso<+HH.mm>");
        string read(string path1, string path2)
        {
            var path = Path.Combine(InputPath, path1, path2);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        foreach (var node in nodes.Values)
        {
            var commit = node.Commit;
            var parsed = Regex.Match(commit.DirName, @"^(?<tm>\d\d\d\d\.\d\d\.\d\d--\d\d\.\d\d\.\d\d[+-]\d\d\.\d\d)--");
            if (!parsed.Success)
                throw new TellUserException(1, $"Cannot parse commit directory name: {{yellow}}{commit.DirName}{{}}");
            commit.AuthorTime = timePattern.Parse(parsed.Groups["tm"].Value).Value;
            commit.Author = read(commit.DirName, "author.txt");
            commit.Committer = read(commit.DirName, "committer.txt") ?? commit.Author;
            commit.Author ??= commit.Committer;
            if (commit.Author == null) throw new TellUserException(1, $"No author for commit {{yellow}}{commit.DirName}{{}}.");
            for (int i = 0; ; i++)
            {
                var parent = read(commit.DirName, $"parent{i}.txt");
                if (parent == null) break;
                commit.Parents.Add(parent);
                if (!nodes.ContainsKey(parent)) throw new TellUserException(1, $"Cannot resolve commit name {{yellow}}{parent}{{}} used by {{h}}parent{i}{{}} of {{yellow}}{commit.DirName}{{}}.");
                node.Parents.Add(nodes[parent]);
                nodes[parent].Children.Add(node);
            }
            var commitTime = read(commit.DirName, "commit-time.txt");
            if (commitTime == null)
                commit.CommitTime = commit.AuthorTime;
            else
                commit.CommitTime = timePattern.Parse(commitTime).Value;
            commit.Message = read(commit.DirName, "message.txt")?.Split("\r\n", "\n").ToList() ?? throw new TellUserException(1, $"No commit message for commit {{yellow}}{commit.DirName}{{}}.");
        }
        // Parse refs
        var refs = new List<(string name, CommitNode node)>();
        void findRefs(DirectoryInfo dir, string name)
        {
            if (!dir.Exists) return;
            foreach (var f in dir.EnumerateFiles())
            {
                var reftarget = read(dir.FullName, f.Name);
                var refname = name + "/" + f.Name;
                if (!nodes.ContainsKey(reftarget)) throw new TellUserException(1, $"Cannot resolve commit name {{yellow}}{reftarget}{{}} used by {{cyan}}{refname}{{}}.");
                refs.Add((refname, nodes[reftarget]));
            }
            foreach (var d in dir.EnumerateDirectories())
                findRefs(d, name + "/" + d.Name);
        }
        findRefs(new DirectoryInfo(Path.Combine(InputPath, "refs")), "refs");

        // Topological sort
        var done = new HashSet<CommitNode>();
        var sorted = new List<CommitNode>();
        void add(CommitNode node)
        {
            if (done.Contains(node)) return;
            foreach (var p in node.Parents)
                add(p);
            sorted.Add(node);
            done.Add(node);
        }
        foreach (var node in nodes.Values)
            add(node);

        // Execute
        if (!Directory.Exists(OutputRepo))
        {
            Directory.CreateDirectory(OutputRepo);
            git(OutputRepo, null, "init", ".");
        }
        foreach (var node in sorted)
        {
            ConsoleUtil.Write(C($"Writing commit {{yellow}}{node.Commit.DirName}{{}}..."));
            // Copy and commit tree - TODO: git 2.42 supports creating a worktree on an orphan branch, so we can use worktrees and bypass all the copying
            cleanWorkdir(OutputRepo);
            File.Delete(Path.Combine(OutputRepo, ".git", "index")); // force add -A to rebuild the index - otherwise we get wrong trees sometimes!
            copyContents(new DirectoryInfo(Path.Combine(InputPath, node.Commit.DirName, "tree")), new DirectoryInfo(OutputRepo));
            git(OutputRepo, null, "add", "-A");
            var treeId = git(OutputRepo, null, "write-tree").Trim();
            // Construct commit
            var lines = new List<string>();
            lines.Add("tree " + treeId);
            foreach (var parent in node.Parents)
            {
                if (parent.Commit.Id == null) throw new Exception("Expected commit ID to be known");
                lines.Add("parent " + parent.Commit.Id);
            }
            lines.Add($"author {node.Commit.Author} {node.Commit.AuthorTime.ToInstant().ToUnixTimeSeconds()} {node.Commit.AuthorTime:o<+HHmm>}");
            lines.Add($"committer {node.Commit.Committer} {node.Commit.CommitTime.ToInstant().ToUnixTimeSeconds()} {node.Commit.CommitTime:o<+HHmm>}");
            lines.Add("");
            lines.AddRange(node.Commit.Message);
            // Write commit object
            node.Commit.Id = git(OutputRepo, lines.JoinString("\n").ToUtf8(), "hash-object", "-t", "commit", "-w", "--stdin").Trim();
            ConsoleUtil.WriteLine(C($" {{green}}{node.Commit.Id.SubstringSafe(0, 8)}{{}}"));
        }
        // Write refs
        Console.WriteLine("Writing refs...");
        foreach (var r in refs)
        {
            ConsoleUtil.WriteLine(C($"    {{cyan}}{r.name}{{}} -> {{green}}{r.node.Commit.Id.SubstringSafe(0, 8)}{{}}"));
            git(OutputRepo, null, "update-ref", r.name, r.node.Commit.Id);
        }

        void cleanWorkdir(string path)
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles())
                file.Delete();
            foreach (var d in dir.EnumerateDirectories())
                if (d.Name != ".git")
                    d.Delete(recursive: true);
        }
        void copyContents(DirectoryInfo from, DirectoryInfo to)
        {
            to.Create();
            foreach (var file in from.EnumerateFiles())
                file.CopyTo(Path.Combine(to.FullName, file.Name), overwrite: false); // we only copy into clean directories
            foreach (var dir in from.EnumerateDirectories())
                copyContents(dir, new DirectoryInfo(Path.Combine(to.FullName, dir.Name)));
        }

        return 0;
    }
}

class CommitNode
{
    public Commit Commit;
    public List<CommitNode> Parents = new();
    public List<CommitNode> Children = new();

    public override string ToString() => $"node: {Commit}";
}

class Commit
{
    public string Id;
    public string Tree;
    public List<string> Parents = new();
    public string Author, Committer;
    public OffsetDateTime AuthorTime, CommitTime;
    public List<string> GpgSig;
    public List<string> Message = new();
    public string DirName;

    public override string ToString() => $"{(Id ?? "").SubstringSafe(0, 8)} - {AuthorTime} - {Message.JoinString().SubstringSafe(0, 30)}";

    public static Commit ParseRawCommit(string id, string raw)
    {
        OffsetDateTime gtime(Match m)
        {
            var utc = Instant.FromUnixTimeSeconds(int.Parse(m.Groups["ud"].Value)); // we don't preserve the original offset when reading
            return utc.InZone(DateTimeZoneProviders.Bcl.GetSystemDefault()).ToOffsetDateTime();
        }

        var commit = new Commit();
        commit.Id = id;
        var lines = raw.Split(new[] { "\n" }, StringSplitOptions.None);
        int cur = 0;

        if (!lines[cur].StartsWith("tree ")) throw new Exception("Expected 'tree'");
        commit.Tree = lines[cur][5..];
        cur++;

        while (lines[cur].StartsWith("parent "))
        {
            commit.Parents.Add(lines[cur][7..]);
            cur++;
        }

        if (!lines[cur].StartsWith("author ")) throw new Exception("Expected 'author'");
        var match = Regex.Match(lines[cur], @"^author (?<a>.*?) (?<ud>\d+) (?<tz>[+-]\d\d\d\d)$");
        if (!match.Success) throw new Exception("Couldn't parse 'author'");
        commit.Author = match.Groups["a"].Value;
        commit.AuthorTime = gtime(match);
        cur++;

        if (!lines[cur].StartsWith("committer ")) throw new Exception("Expected 'committer'");
        match = Regex.Match(lines[cur], @"^committer (?<a>.*?) (?<ud>\d+) (?<tz>[+-]\d\d\d\d)$");
        if (!match.Success) throw new Exception("Couldn't parse 'committer'");
        commit.Committer = match.Groups["a"].Value;
        commit.CommitTime = gtime(match);
        cur++;

        if (lines[cur].StartsWith("HG:"))
            cur++;

        if (lines[cur] == "gpgsig -----BEGIN PGP SIGNATURE-----")
        {
            Console.WriteLine("Found gpgsig; this won't be preserved. Reassembly of affected commit will not have the signature.");
            commit.GpgSig = new();
            while (true)
            {
                commit.GpgSig.Add(lines[cur]);
                if (lines[cur] == " -----END PGP SIGNATURE-----")
                {
                    cur++;
                    if (lines[cur] != " ") throw new Exception("Expected blank line with a space after end pgp signature");
                    commit.GpgSig.Add(lines[cur]);
                    cur++;
                    break;
                }
                cur++;
            }
        }

        if (lines[cur] != "") throw new Exception("Expected blank line after all known commit properties");
        cur++;

        for (int c = cur; c < lines.Length; c++)
            commit.Message.Add(lines[c]);

        return commit;
    }
}

class TellUserException : Exception
{
    public int ExitCode { get; private set; }
    public TellUserException(int exitcode, string message) : base(message)
    {
        ExitCode = exitcode;
    }
}
