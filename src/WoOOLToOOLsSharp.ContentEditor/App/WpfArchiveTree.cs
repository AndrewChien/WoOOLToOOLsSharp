using System;
using System.Collections.Generic;
using System.IO;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.ContentEditor.App;

public sealed class WpfArchiveTree
{
    public Dictionary<string, WpfTreeNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WpfEntry> EntryByFullPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static WpfArchiveTree BuildFromEntries(IReadOnlyList<WpfEntry> entries)
    {
        var tree = new WpfArchiveTree();
        tree.Nodes[string.Empty] = new WpfTreeNode { FullPath = string.Empty };

        if (entries is null)
        {
            return tree;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            WpfEntry e = entries[i];
            if (e is null) continue;

            if (!tree.EntryByFullPath.ContainsKey(e.FullPath))
            {
                tree.EntryByFullPath[e.FullPath] = e;
            }

            if (e.IsDirectory)
            {
                EnsureNode(tree, e.FullPath);
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            WpfEntry e = entries[i];
            if (e is null) continue;
            if (string.IsNullOrWhiteSpace(e.FullPath)) continue; // root entry

            string parent = WpfKey.GetDirectoryPath(e.FullPath);
            WpfTreeNode parentNode = EnsureNode(tree, parent);

            if (e.IsDirectory)
            {
                parentNode.ChildDirs.Add(e);
                continue;
            }

            parentNode.ChildFiles.Add(e);

            try
            {
                string ext = Path.GetExtension(e.FullPath);
                if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                {
                    parentNode.TexChildren.Add(e);
                }
            }
            catch
            {
                // ignore invalid paths; still keep in ChildFiles
            }
        }

        foreach (WpfTreeNode node in tree.Nodes.Values)
        {
            node.ChildDirs.Sort(static (a, b) =>
                string.Compare(a?.Name, b?.Name, StringComparison.OrdinalIgnoreCase));
            node.ChildFiles.Sort(static (a, b) =>
                string.Compare(a?.Name, b?.Name, StringComparison.OrdinalIgnoreCase));
            node.TexChildren.Sort(static (a, b) =>
                string.Compare(a?.Name, b?.Name, StringComparison.OrdinalIgnoreCase));
        }

        return tree;
    }

    private static WpfTreeNode EnsureNode(WpfArchiveTree tree, string fullPath)
    {
        if (!tree.Nodes.TryGetValue(fullPath, out WpfTreeNode? node))
        {
            node = new WpfTreeNode { FullPath = fullPath ?? string.Empty };
            tree.Nodes[fullPath ?? string.Empty] = node;
        }

        return node;
    }
}

public sealed class WpfTreeNode
{
    public string FullPath { get; init; } = string.Empty; // Directory path inside WPF; "" means root
    public List<WpfEntry> ChildDirs { get; } = new();
    public List<WpfEntry> ChildFiles { get; } = new();

    /// <summary>Direct .tex file children (used by old project for pseudo-SGL open).</summary>
    public List<WpfEntry> TexChildren { get; } = new();
}

