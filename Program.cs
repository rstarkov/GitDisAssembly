using System.Text.RegularExpressions;
using NodaTime;
using RT.CommandLine;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace GitPatchDisAssemble;

class Program
{
    static CmdLine Args;

    static void Main(string[] args)
    {
        Args = CommandLineParser.ParseOrWriteUsageToConsole<CmdLine>(args);
        if (Args == null)
            return;
        Args.Execute();
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
            throw new Exception();
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
        Console.WriteLine($"Found {allrefs.Count} refs: " + allrefs.Select(x => x.rf).JoinString(", "));

        var allobjects = splitNewlineTerminated(git(InputRepo, null, "cat-file", "--batch-check", "--batch-all-objects", "--unordered")).Select(r => r.Split(' ')).ToList();
        var commitIds = allobjects.Where(r => r[1] == "commit").Select(r => r[0]).ToList();
        Console.WriteLine($"Found {commitIds.Count} commit objects");

        Console.WriteLine($"Reading every commit...");
        var commits = commitIds.AsParallel().WithDegreeOfParallelism(10).Select(cid => Commit.ParseRawCommit(cid, git(InputRepo, null, "cat-file", "-p", cid))).ToList();
        Console.WriteLine($"Processing...");
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
                throw new Exception($"The value '{addRef}' passed to AddRefs is not a known commit or ref name. For refs, use full names (such as refs/heads/main).");
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
            if (!discovered.Contains(nodes[r.id]))
                continue;
            var path = Path.Combine(OutputPath, r.rf);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, nodes[r.id].Commit.DirName);
        }

        // write out commits
        foreach (var c in discovered)
        {
            Console.WriteLine($"Writing {c.Commit.DirName}");
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
            throw new Exception();
        return lines[..^1].Split('\n');
    }
}

class CommitNode
{
    public Commit Commit;
    public List<CommitNode> Parents;
    public List<CommitNode> Children = new();
}

class Commit
{
    public string Id;
    public string Tree;
    public List<string> Parents = new();
    public string Author, Committer;
    public OffsetDateTime AuthorTime, CommitTime;
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

        if (!lines[cur].StartsWith("tree ")) throw new Exception();
        commit.Tree = lines[cur][5..];
        cur++;

        while (lines[cur].StartsWith("parent "))
        {
            commit.Parents.Add(lines[cur][7..]);
            cur++;
        }

        if (!lines[cur].StartsWith("author ")) throw new Exception();
        var match = Regex.Match(lines[cur], @"^author (?<a>.*?) (?<ud>\d+) (?<tz>[+-]\d\d\d\d)$");
        if (!match.Success) throw new Exception();
        commit.Author = match.Groups["a"].Value;
        commit.AuthorTime = gtime(match);
        cur++;

        if (!lines[cur].StartsWith("committer ")) throw new Exception();
        match = Regex.Match(lines[cur], @"^committer (?<a>.*?) (?<ud>\d+) (?<tz>[+-]\d\d\d\d)$");
        if (!match.Success) throw new Exception();
        commit.Committer = match.Groups["a"].Value;
        commit.CommitTime = gtime(match);
        cur++;

        if (lines[cur].StartsWith("HG:"))
            cur++;

        if (lines[cur] != "") throw new Exception();
        cur++;

        for (int c = cur; c < lines.Length; c++)
            commit.Message.Add(lines[c]);

        return commit;
    }
}
