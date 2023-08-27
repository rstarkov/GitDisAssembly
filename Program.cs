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

    protected string git(string repo, params string[] args)
    {
        var arglist = new List<string>();
        arglist.Add(GitExe);
        arglist.Add("-c");
        arglist.Add("core.autocrlf=" + (AutoCrLf ? "true" : "false"));
        arglist.AddRange(args);
        var output = CommandRunner.Run(arglist.ToArray()).WithWorkingDirectory(repo).OutputNothing().SuccessExitCodes(0).GoGetOutputText();
        if (output.EndsWith("\r\n"))
            throw new Exception();
        return output;
    }
}

[CommandName("dis", "d")]
class CmdDisassemble : CmdLine
{
    [IsPositional]
    public string InputRepo = null;
    [IsPositional]
    public string OutputPath = null;
    [IsPositional]
    public string[] AdditionalRefs = null;

    // customization todo: folder name template; whether to use author or commit time for folders; zulu times + timezone

    public override int Execute()
    {
        var allrefs = splitNewlineTerminated(git(InputRepo, "show-ref")).Select(r => (id: r.Split(' ')[0], rf: r.Split(' ')[1])).ToList();
        var ref2id = allrefs.ToDictionary(x => x.rf, x => x.id);
        Console.WriteLine($"Found {allrefs.Count} refs: " + allrefs.Select(x => x.rf).JoinString(", "));
        var fromIds = AdditionalRefs.Select(r => ref2id.ContainsKey(r) ? ref2id[r] : r).ToList();

        var allobjects = splitNewlineTerminated(git(InputRepo, "cat-file", "--batch-check", "--batch-all-objects", "--unordered")).Select(r => r.Split(' ')).ToList();
        var commitIds = allobjects.Where(r => r[1] == "commit").Select(r => r[0]).ToList();
        Console.WriteLine($"Found {commitIds.Count} commit objects");

        Console.WriteLine($"Reading every commit...");
        var commits = commitIds.AsParallel().WithDegreeOfParallelism(10).Select(cid => Commit.ParseRawCommit(cid, git(InputRepo, "cat-file", "-p", cid))).ToList();
        Console.WriteLine($"Processing...");
        var nodes = commits.ToDictionary(c => c.Id, c => new CommitNode { Commit = c });
        foreach (var node in nodes.Values)
        {
            node.Parents = node.Commit.Parents.Select(p => nodes[p]).ToList();
            foreach (var p in node.Parents)
                p.Children.Add(node);
        }

        var discovered = new HashSet<CommitNode>();
        void discoverAdd(CommitNode n, bool withChildren)
        {
            if (!discovered.Add(n))
                return;
            foreach (var p in n.Parents)
                discoverAdd(p, withChildren);
            if (withChildren)
                foreach (var c in n.Children)
                    discoverAdd(c, withChildren);
        }
        foreach (var r in allrefs)
            discoverAdd(nodes[r.id], false);
        foreach (var id in fromIds)
            discoverAdd(nodes[id], false);

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
            c.Commit.FileName = $"{time(c.Commit.AuthorTime):yyyy.MM.dd--HH.mm.sso<+HH.mm>}--{c.Commit.Id[..8]}--{msgpreview(c.Commit.Message)}";

        // write out refs
        Directory.CreateDirectory(OutputPath);
        foreach (var r in allrefs)
        {
            var path = Path.Combine(OutputPath, r.rf);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, nodes[r.id].Commit.FileName);
        }

        // write out commits
        foreach (var c in discovered)
        {
            Console.WriteLine($"Writing {c.Commit.FileName}");
            var path = Path.Combine(OutputPath, c.Commit.FileName);
            Directory.CreateDirectory(path);
            git(InputRepo, "worktree", "add", Path.Combine(path, "temptree"), c.Commit.Id);
            Directory.Move(Path.Combine(path, "temptree"), Path.Combine(path, "tree"));
            File.Delete(Path.Combine(path, "tree", ".git"));
            git(InputRepo, "worktree", "prune");
            File.WriteAllText(Path.Combine(path, "message.txt"), c.Commit.Message.JoinString("\r\n"));
            for (int pi = 0; pi < c.Parents.Count; pi++)
                File.WriteAllText(Path.Combine(path, $"parent{pi}.txt"), c.Parents[pi].Commit.FileName);
            File.WriteAllText(Path.Combine(path, "author.txt"), c.Commit.Author);
            if (c.Commit.Committer != c.Commit.Author)
                File.WriteAllText(Path.Combine(path, "committer.txt"), c.Commit.Committer);
            if (c.Commit.CommitTime != c.Commit.AuthorTime)
                File.WriteAllText(Path.Combine(path, "commit-time.txt"), $"{time(c.Commit.CommitTime):yyyy.MM.dd--HH.mm.sso<+HH.mm>}");
        }

        return 0;
    }

    static OffsetDateTime time(Instant time)
    {
        return time.InZone(DateTimeZoneProviders.Bcl.GetSystemDefault()).ToOffsetDateTime();
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
    public Instant AuthorTime, CommitTime;
    public List<string> Message = new();
    public string FileName;

    public static Commit ParseRawCommit(string id, string raw)
    {
        Instant gtime(Match m)
        {
            var utc = Instant.FromUnixTimeSeconds(int.Parse(m.Groups["ud"].Value));
            //var offset = int.Parse(m.Groups["tz"].Value);
            return utc; // ignore offset, convert to a specific timezone on display
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
