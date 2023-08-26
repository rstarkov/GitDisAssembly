using System.Text;
using System.Text.RegularExpressions;
using NodaTime;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace GitPatchDisAssemble;

class Program
{
    static string Git = @"C:\Apps\Git\cmd\git.exe";
    static string Repo;

    static void Main(string[] args)
    {
        Repo = args[0];
        var to = args[1];
        var from = args[2]; // should really accept multiple

        Console.WriteLine($"Processing...");
        var allrefs = splitNewlineTerminated(git("show-ref")).Select(r => (id: r.Split(' ')[0], rf: r.Split(' ')[1])).ToList();
        var ref2id = allrefs.ToDictionary(x => x.rf, x => x.id);
        //var id2ref = allrefs.ToDictionary(x => x.id, x => x.rf);
        var fromId = ref2id.ContainsKey(from) ? ref2id[from] : from;

        var allobjects = splitNewlineTerminated(git("cat-file", "--batch-check", "--batch-all-objects", "--unordered")).Select(r => r.Split(' ')).ToList();
        var commitIds = allobjects.Where(r => r[1] == "commit").Select(r => r[0]).ToList();
        //var commitIds = splitNewlineTerminated(git("rev-list", "--all"));

        Console.WriteLine($"Reading every commit...");
        var commits = commitIds.AsParallel().WithDegreeOfParallelism(10).Select(cid => Commit.ParseRawCommit(cid, git("cat-file", "-p", cid))).ToList();
        Console.WriteLine($"Processing...");
        var nodes = commits.ToDictionary(c => c.Id, c => new CommitNode { Commit = c });
        foreach (var node in nodes.Values)
        {
            node.Parents = node.Commit.Parents.Select(p => nodes[p]).ToList();
            foreach (var p in node.Parents)
                p.Children.Add(node);
        }

        // find all commits from the "from" ref
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
        discoverAdd(nodes[fromId], false);
        foreach (var r in allrefs)
            discoverAdd(nodes[r.id], false);

        string msgpreview(IEnumerable<string> msg)
        {
            var s = new string(string.Join(" ", msg).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '.').ToArray());
            s = s.Replace("....", "."); // lazy...
            s = s.Replace("...", ".");
            for (int i = 0; i < 10; i++)
                s = s.Replace("..", ".");
            return s.SubstringSafe(0, 20).Trim('.');
        }

        // write out commits
        if (true)
        {
            foreach (var c in discovered)
                c.Commit.FileName = $"{time(c.Commit.AuthorTime):yyyy.MM.dd--HH.mm.sso<+HH.mm>}--{c.Commit.Id[..8]}--{msgpreview(c.Commit.Message)}";
            Directory.CreateDirectory(to);
            foreach (var c in discovered)
            {
                Console.WriteLine($"Writing {c.Commit.FileName}");
                var path = Path.Combine(to, c.Commit.FileName);
                Directory.CreateDirectory(path);
                git("worktree", "add", Path.Combine(path, "temptree"), c.Commit.Id);
                Directory.Move(Path.Combine(path, "temptree"), Path.Combine(path, "tree"));
                File.Delete(Path.Combine(path, "tree", ".git"));
                git("worktree", "prune");
                var commitstr = new StringBuilder();
                //commitstr.AppendLine($"tree {c.Commit.Tree}");
                foreach (var p in c.Parents)
                    commitstr.AppendLine($"parent {p.Commit.FileName}");
                commitstr.AppendLine($"author {c.Commit.Author}");
                commitstr.AppendLine("");
                foreach (var msg in c.Commit.Message)
                    commitstr.AppendLine(msg);
                File.WriteAllText(Path.Combine(path, "commit.txt"), commitstr.ToString());
            }
        }
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

    static string git(params string[] args)
    {
        var output = CommandRunner.Run(new[] { Git }.Concat(args).ToArray()).WithWorkingDirectory(Repo).OutputNothing().SuccessExitCodes(0).GoGetOutputText();
        if (output.EndsWith("\r\n"))
            throw new Exception();
        return output;
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
    public Instant AuthorTime, CommitterTime;
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
        commit.CommitterTime = gtime(match);
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