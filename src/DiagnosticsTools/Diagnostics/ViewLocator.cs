using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Diagnostics.ViewModels;

namespace Avalonia.Diagnostics
{
    public class ViewLocator : IDataTemplate
    {
        public Control? Build(object? data)
        {
            if (data is null)
                return null;

            var viewType = ResolveViewType(data.GetType());

            if (viewType != null)
            {
                return (Control)Activator.CreateInstance(viewType)!;
            }
            else
            {
                return new TextBlock { Text = data.GetType().FullName };
            }
        }

        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }

        private static Type? ResolveViewType(Type viewModelType)
        {
            var assembly = viewModelType.Assembly;
            var simpleName = viewModelType.Name.Replace("ViewModel", "View");

            var directName = viewModelType.FullName!.Replace("ViewModel", "View");
            var viewType = assembly.GetType(directName);
            if (viewType != null)
            {
                return viewType;
            }

            if (viewModelType.Namespace is { } ns)
            {
                var swappedNamespace = ns.Replace(".ViewModels", ".Views");
                if (!ReferenceEquals(swappedNamespace, ns))
                {
                    var alternateName = string.Concat(swappedNamespace, ".", simpleName);
                    viewType = assembly.GetType(alternateName);
                    if (viewType != null)
                    {
                        return viewType;
                    }
                }
            }

            foreach (var candidate in assembly.GetTypes())
            {
                if (string.Equals(candidate.Name, simpleName, StringComparison.Ordinal) &&
                    candidate.Namespace is { } candidateNamespace &&
                    candidateNamespace.Contains(".Views"))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
