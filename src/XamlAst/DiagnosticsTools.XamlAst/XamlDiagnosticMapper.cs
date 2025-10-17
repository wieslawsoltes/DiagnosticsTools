using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.Xaml
{
    public static class XamlDiagnosticMapper
    {
        public static IReadOnlyList<XamlAstDiagnostic> CollectDiagnostics(XmlDocumentSyntax syntax)
        {
            if (syntax is null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            var result = new List<XamlAstDiagnostic>();
            var stack = new Stack<SyntaxNode>();
            stack.Push(syntax);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                var diagnostics = GetDiagnostics(node);

                if (diagnostics.Length > 0)
                {
                    foreach (var diagnostic in diagnostics)
                    {
                        result.Add(new XamlAstDiagnostic(
                            node.Span,
                            MapSeverity(diagnostic),
                            GetErrorId(diagnostic),
                            GetDescription(diagnostic)));
                    }
                }

                foreach (var child in node.ChildNodes)
                {
                    stack.Push(child);
                }
            }

            return result.Count == 0 ? Array.Empty<XamlAstDiagnostic>() : result;
        }

        private static object[] GetDiagnostics(SyntaxNode node)
        {
            try
            {
                var method = node.GetType().GetMethod("GetDiagnostics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.DefaultBinder, Type.EmptyTypes, Array.Empty<ParameterModifier>());
                if (method is not null && method.Invoke(node, Array.Empty<object?>()) is Array array && array.Length > 0)
                {
                    var diagnostics = new object[array.Length];
                    array.CopyTo(diagnostics, 0);
                    return diagnostics;
                }
            }
            catch
            {
            }

            return Array.Empty<object>();
        }

        private static XamlDiagnosticSeverity MapSeverity(object diagnostic)
        {
            // GuiLabs diagnostics currently expose only errors; expose future severities when available.
            return XamlDiagnosticSeverity.Error;
        }

        private static ERRID GetErrorId(object diagnostic)
        {
            try
            {
                var property = diagnostic.GetType().GetProperty("ErrorID", BindingFlags.Public | BindingFlags.Instance);
                if (property is not null && property.GetValue(diagnostic) is ERRID errId)
                {
                    return errId;
                }
            }
            catch
            {
            }

            return ERRID.ERR_None;
        }

        private static string GetDescription(object diagnostic)
        {
            try
            {
                var method = diagnostic.GetType().GetMethod("GetDescription", BindingFlags.Public | BindingFlags.Instance, Type.DefaultBinder, Type.EmptyTypes, Array.Empty<ParameterModifier>());
                if (method is not null && method.Invoke(diagnostic, Array.Empty<object?>()) is string message && !string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch
            {
            }

            return diagnostic?.ToString() ?? string.Empty;
        }
    }
}
