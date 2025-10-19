using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics.Controls.VirtualizedTreeView;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Reactive;
using System.Linq;

namespace Avalonia.Diagnostics.ViewModels
{
    public abstract class TreeNode : ViewModelBase, ITreeNode, IDisposable, IMutableTreeNode
    {
        private readonly IDisposable? _classesSubscription;
        private string _classes;
        private bool _isExpanded;
        private bool _isVisible = true;
        private SourceInfo? _sourceInfo;
        private XamlAstNodeDescriptor? _xamlDescriptor;
        private bool _isMultiSelected;
        private bool _isSearchMatch;

        protected TreeNode(AvaloniaObject avaloniaObject, TreeNode? parent, string? customTypeName = null)
        {
            _classes = string.Empty;
            Parent = parent;
            Type = customTypeName ?? avaloniaObject.GetType().Name;
            Visual = avaloniaObject;
            FontWeight = IsRoot ? FontWeight.Bold : FontWeight.Normal;

            ElementName = (avaloniaObject as INamed)?.Name;

            if (avaloniaObject is StyledElement { Classes: { } classes })
            {
                _classesSubscription = ((IObservable<object?>)classes.GetWeakCollectionChangedObservable())
                    .StartWith(null)
                    .Subscribe(_ =>
                    {
                        if (classes.Count > 0)
                        {
                            Classes = $"({string.Join(" ", classes)})";
                        }
                        else
                        {
                            Classes = string.Empty;
                        }
                    });
            }
        }

        private bool IsRoot => Visual is TopLevel ||
                               Visual is ContextMenu ||
                               Visual is IPopupHost;

        public FontWeight FontWeight { get; }

        public bool HasChildren => Children.Count > 0;

        IReadOnlyList<ITreeNode> ITreeNode.Children => Children;

        public abstract TreeNodeCollection Children
        {
            get;
        }

        public string Classes
        {
            get { return _classes; }
            private set { RaiseAndSetIfChanged(ref _classes, value); }
        }

        public string? ElementName
        {
            get;
        }

        public virtual bool IsInTemplate => false;

        public AvaloniaObject Visual
        {
            get;
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { RaiseAndSetIfChanged(ref _isExpanded, value); }
        }

        public bool IsVisible
        {
            get { return _isVisible; }
            set { RaiseAndSetIfChanged(ref _isVisible, value); }
        }

        public TreeNode? Parent
        {
            get;
        }

        IMutableTreeNode? IMutableTreeNode.Parent => Parent;

        public string Type
        {
            get;
            private set;
        }

        public virtual string SearchText
        {
            get
            {
                var type = Type;
                var elementName = ElementName;
                var classes = string.IsNullOrWhiteSpace(Classes)
                    ? null
                    : Classes.Trim('(', ')');
                var visualType = Visual?.GetType();

                return string.Join(" ", new[]
                    {
                        type,
                        elementName,
                        classes,
                        visualType?.Name,
                        visualType?.FullName,
                    }.Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x!.Trim()));
            }
        }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public SourceInfo? SourceInfo
        {
            get => _sourceInfo;
            private set
            {
                if (RaiseAndSetIfChanged(ref _sourceInfo, value))
                {
                    RaisePropertyChanged(nameof(SourceSummary));
                    RaisePropertyChanged(nameof(HasSource));
                }
            }
        }

        public XamlAstNodeDescriptor? XamlDescriptor
        {
            get => _xamlDescriptor;
            private set => RaiseAndSetIfChanged(ref _xamlDescriptor, value);
        }

        public string? SourceSummary => SourceInfo?.DisplayPath;

        public bool HasSource => SourceInfo is not null;

        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            internal set => RaiseAndSetIfChanged(ref _isMultiSelected, value);
        }

        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            internal set => RaiseAndSetIfChanged(ref _isSearchMatch, value);
        }

        public void Dispose()
        {
            _classesSubscription?.Dispose();
            Children.CollectionChanged -= OnChildrenCollectionChanged;
            Children.Dispose();
        }

        protected TreeNodeCollection RegisterChildren(TreeNodeCollection collection)
        {
            collection.CollectionChanged += OnChildrenCollectionChanged;
            return collection;
        }

        private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            CollectionChanged?.Invoke(this, e);
            RaisePropertyChanged(nameof(HasChildren));
        }

        internal void UpdateSourceInfo(SourceInfo? info)
        {
            SourceInfo = info;
        }

        internal void UpdateXamlDescriptor(XamlAstNodeDescriptor? descriptor)
        {
            XamlDescriptor = descriptor;
        }
    }
}
