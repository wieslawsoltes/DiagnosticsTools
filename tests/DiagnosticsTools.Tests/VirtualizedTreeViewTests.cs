using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Diagnostics.Controls.VirtualizedTreeView;
using Xunit;

namespace DiagnosticsTools.Tests;

public class VirtualizedTreeViewTests
{
    private class TestTreeNode : ITreeNode
    {
        public TestTreeNode(string name, params TestTreeNode[] children)
        {
            Name = name;
            Children = children;
        }

        public string Name { get; }
        public bool IsExpanded { get; set; }
        public bool HasChildren => Children.Count > 0;
        public IReadOnlyList<ITreeNode> Children { get; }
        public bool IsVisible { get; set; } = true;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
    }

    [Fact]
    public void FlatTree_WithMultipleRoots_ShouldContainAllRoots()
    {
        // Arrange
        var root1 = new TestTreeNode("Root1");
        var root2 = new TestTreeNode("Root2");
        var root3 = new TestTreeNode("Root3");
        var roots = new[] { root1, root2, root3 };

        // Act
        var flatTree = new FlatTree(roots);

        // Assert
        Assert.Equal(3, flatTree.Count);
        Assert.Equal("Root1", ((TestTreeNode)flatTree[0].Node).Name);
        Assert.Equal("Root2", ((TestTreeNode)flatTree[1].Node).Name);
        Assert.Equal("Root3", ((TestTreeNode)flatTree[2].Node).Name);
    }

    [Fact]
    public void FlatTree_WithExpandedRoot_ShouldIncludeChildren()
    {
        // Arrange
        var child1 = new TestTreeNode("Child1");
        var child2 = new TestTreeNode("Child2");
        var root = new TestTreeNode("Root", child1, child2) { IsExpanded = true };
        var roots = new[] { root };

        // Act
        var flatTree = new FlatTree(roots);

        // Assert
        Assert.Equal(3, flatTree.Count); // Root + 2 children
        Assert.Equal("Root", ((TestTreeNode)flatTree[0].Node).Name);
        Assert.Equal("Child1", ((TestTreeNode)flatTree[1].Node).Name);
        Assert.Equal("Child2", ((TestTreeNode)flatTree[2].Node).Name);
    }

    [Fact]
    public void FlatTree_WithMultipleExpandedRoots_ShouldIncludeAllChildren()
    {
        // Arrange
        var child1 = new TestTreeNode("Child1");
        var child2 = new TestTreeNode("Child2");
        var root1 = new TestTreeNode("Root1", child1) { IsExpanded = true };
        var root2 = new TestTreeNode("Root2", child2) { IsExpanded = true };
        var roots = new[] { root1, root2 };

        // Act
        var flatTree = new FlatTree(roots);

        // Assert
        Assert.Equal(4, flatTree.Count); // 2 roots + 2 children
        Assert.Equal("Root1", ((TestTreeNode)flatTree[0].Node).Name);
        Assert.Equal("Child1", ((TestTreeNode)flatTree[1].Node).Name);
        Assert.Equal("Root2", ((TestTreeNode)flatTree[2].Node).Name);
        Assert.Equal("Child2", ((TestTreeNode)flatTree[3].Node).Name);
    }
}
