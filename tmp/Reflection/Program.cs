using System;
using System.Linq;
using Microsoft.Language.Xml;

var syntax = Parser.ParseText("<Grid></Grid>");
var element = (XmlElementSyntax)syntax.RootSyntax;
var endTag = element.EndTag;
Console.WriteLine(endTag.GetType().GetProperty("LessThanSlashToken") != null);
Console.WriteLine(endTag.GetType().GetProperty("LessThanSlashToken")?.Name);
Console.WriteLine(endTag.GetType().GetProperty("LessThanSlashToken") != null);
Console.WriteLine(endTag.GetType().GetProperty("LessThanSlashToken")?.Name);
Console.WriteLine(endTag.GetType().GetProperty("LessThanSlashToken") == null);
Console.WriteLine(string.Join(",", endTag.GetType().GetProperties().Select(p => p.Name)));
