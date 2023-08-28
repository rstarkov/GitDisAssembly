# GitDisAssemble

Disassembles a git repository into a folder structure representing commits with editable text files, and re-assembles those back into a git repository.

A disassembly followed by a reassembly produces identical commit hashes.

GitDisAssemble operates by invoking your `git` executable. Specify the path to your git with `-ge <path>`.

Make sure to read the **disclaimer** at the bottom before attempting to use GitDisAssemble!

## Why?

To edit all commits however you please, using file management tools.

## Typical usage

`GitDisAssemble.exe disassemble --all-heads C:\code\MyGitRepo C:\temp\DisassembledRepo`

`GitDisAssemble.exe assemble C:\temp\DisassembledRepo C:\code\NewGitRepo`

### Disassembled structure

On a low level, a git commit is a small text file referencing (by hash) a tree of files, zero or more parent commits, plus some basic metadata –
namely, author name/date, committer name/date, and the commit message.

When disassembled, a commit becomes a directory named after the author date, and including original truncated hash and commit message for ease
of navigation. This directory contains the file tree, and the metadata spread across several plaintext files:

```
  - 2023.08.26--14.06.44+01.00--a89aaf93--Initial [dir]
    - tree [dir]
    - author.txt
    - message.txt
  - 2023.08.26--14.07.22+01.00--73449d5f--Made.some.changes [dir]
    - tree [dir]
    - author.txt
    - message.txt
    - parent0.txt
  - refs [dir]
    - heads [dir]
      - main
    - tags [dir]
      - foo-1.0
```

The timestamp in the directory name is significant: it is parsed on assembly and becomes the author time. The rest of the directory
name is irrelevant, except that the whole directory name must be used when referencing a commit in `parentN.txt` and `refs` files.

`message.txt` is mandatory; it contains the commit message. When assembling a repo, GitDisAssemble normalises newlines to Unix format
regardless of how this file is saved. Trailing newlines are significant and will impact commit hash.

`author.txt` and `committer.txt` contain the author or committer name, for example `John Smith <john@example.com>`. One of these files
must be present, but the other is optional – if one of them is missing, the contents of the other one are used for the corresponding
property. On disassembly, GitDisAssemble will only create the `author.txt` when author and committer are the same.

`parent0.txt`, `parent1.txt` etc specify commit parents. Zero or more such files can be provided. This file should contain the name
of a commit directory. In the above example it might contain `2023.08.26--14.06.44+01.00--a89aaf93--Initial`.

`commit-time.txt` is optional; when missing, the author time is used for commit time. The format of timestamps in this file is currently
the same as the timestamps in directory names: a local time in a filename-friendly form plus a zone offset. Disassembly only creates this
when it's different to author time (amended commits, some merges etc).

The files inside `refs` contain the name of a commit directory for the commit they point at.

`tree` contains the entire tree of files as of this commit. This means that disassembled repos contain a copy of the entire repository
for every commit, placing major restrictions on the size of repository that can be disassembled.

All of these files except for `message.txt` and those inside `tree` get whitespace-trimmed when read.

### Commits included during disassembly

GitDisAssemble uses low-level git commands to find every commit object in the repository, including those not pointed to by any refs.
It establishes parent-child relationships between them, and offers command-line options to specify which commits to include during disassembly.
By default no commits are included; command line parameters are used to select commits for inclusion. GitDisAssemble recursively includes every
parent commit, and optionally every child commit (useful in limited circumstances).

`--add-heads` includes every head commit (and recursively every reachable parent commit). `--add-tags` includes every tagged commit.
When both options are specified, all commits visible in a typical Git GUI are included for disassembly.

Instead of the above options, it's possible to include only specific commits by passing a list in `<AddRefs>`. Both ref names and commit IDs
are accepted. This parameter can also be used to include commits not reachable from any refs.

### Refs included during disassembly

GitDisAssemble processes all refs in the repository regardless of type, and writes to the `refs` directory every ref pointing to a commit
that was selected for inclusion as described above.

### Fixing line endings

By default GitDisAssemble writes and re-assembles files with `--core.autocrlf=false`. This ensures that no line ending transforms are applied,
and preserves commit IDs regardless of newline status. Specify `--auto-crlf` during both disassembly and reassembly to disable this behaviour
and transform all committed line endings to Unix style. Your global core.autocrlf settings are ignored in both cases.

## Advanced scenarios

### Merging repositories

Commits exported by GitDisAssemble have "no strings attached" to the original repository, apart from the parent commit names. Simply placing
exported commit directories from multiple repositories into one and re-linking the parents will work as one might expect. Note that git commits are
_snapshots_, not _diffs_ – so following these steps will show the file tree exactly as it was in each original commit. To fully merge the
file trees you must somehow merge the `tree` directories to contain the files as required. There are no helper utilities for this in
GitDisAssemble.

Due to how git structures its object store, it's possible to merge unrelated repositories by simply merging the `.git/objects` directories
on a file level. GitDisAssemble will happily read commit graphs out of a repository merged this way, but you might have to supply
`<AddRefs>` to let it find all the commits you are interested in. I don't know how bad of an idea this is, but it works.

### Assemble into pre-existing repository

If the target directory already exists, GitDisAssemble will **delete all files** in it except the `.git` directory. It will add all of its own
commits and will overwrite all refs that are included in the input. It will not delete other existing refs, commits or other objects. This is
not currently very useful because written commits can only reference other commits by directory name. But there is a [wishlist item](https://github.com/rstarkov/GitDisAssembly/issues/1)
to allow using existing commit IDs in `parentN.txt` files, making it possible to assemble commits on top of existing commits.

The target repository must not be bare due to how GitDisAssembly creates tree objects, although this restriction might be lifted in the future
now that Git 2.42+ supports worktrees with orphaned branches.

## Disclaimer

GitDisAssemble may contain data destroying bugs. It has undergone almost no testing whatsoever. It *will* recursively delete all files in
certain directories *without confirmation*. It may completely destroy the directories you point it at, or perhaps even those you don't.
It *will* destroy uncommitted work in the assembly target repository. It *will* make changes to target repo on assembly that *will* result
in git objects getting deleted next time git runs a cleanup. Use at your own risk!
