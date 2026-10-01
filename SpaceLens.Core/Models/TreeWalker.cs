namespace SpaceLens.Core.Models;

public static class TreeWalker
{
    /// <summary>
    /// Depth-first walk without recursion. <paramref name="visit"/> receives each directory below
    /// <paramref name="start"/> and returns false to skip that directory's subtree.
    /// </summary>
    public static void Walk(ScanTree tree, int start, Func<int, bool> visit, CancellationToken cancellationToken = default)
    {
        var stack = new Stack<int>();
        for (int c = tree.Dir(start).FirstChild; c >= 0; c = tree.Dir(c).NextSibling)
        {
            stack.Push(c);
        }

        int visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int d = stack.Pop();
            if (tree.Dir(d).IsRemoved || !visit(d))
            {
                continue;
            }

            for (int c = tree.Dir(d).FirstChild; c >= 0; c = tree.Dir(c).NextSibling)
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>Resolves a path below the tree root, returning -1 when it was not scanned.</summary>
    public static int Resolve(ScanTree tree, string? path) => string.IsNullOrEmpty(path) ? -1 : tree.FindDirectory(path);
}
