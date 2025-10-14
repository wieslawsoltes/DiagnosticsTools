using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length == 0)
{
	Console.WriteLine("Usage: PdbInspector <path-to-portable-pdb>");
	return;
}

var pdbPath = args[0];
if (!File.Exists(pdbPath))
{
	Console.WriteLine($"PDB not found: {pdbPath}");
	return;
}

Console.WriteLine($"Inspecting {pdbPath}");

using var pdbStream = File.OpenRead(pdbPath);
using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
var reader = provider.GetMetadataReader();

Console.WriteLine("Documents:");

foreach (var documentHandle in reader.Documents)
{
	var document = reader.GetDocument(documentHandle);
	var name = reader.GetString(document.Name);
	Console.WriteLine($"  {name}");
}

Console.WriteLine();
Console.WriteLine("Sequence points referencing XAML:");

foreach (var methodHandle in reader.MethodDebugInformation)
{
	var method = reader.GetMethodDebugInformation(methodHandle);
	var definitionHandle = methodHandle.ToDefinitionHandle();
	if (definitionHandle.IsNil)
	{
		continue;
	}

	MethodDefinition methodDefinition;
	try
	{
		methodDefinition = reader.GetMethodDefinition(definitionHandle);
	}
	catch (BadImageFormatException)
	{
		continue;
	}

	string methodName;
	try
	{
		methodName = reader.GetString(methodDefinition.Name);
	}
	catch (BadImageFormatException)
	{
		continue;
	}

	foreach (var sequencePoint in method.GetSequencePoints())
	{
		var docHandle = sequencePoint.Document.IsNil ? method.Document : sequencePoint.Document;
		if (docHandle.IsNil)
		{
			continue;
		}

		var doc = reader.GetDocument(docHandle);
		var name = reader.GetString(doc.Name);

		if (!name.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
		{
			continue;
		}

		if (sequencePoint.IsHidden)
		{
			continue;
		}

		Console.WriteLine($"  {methodName} -> {name} : {sequencePoint.StartLine},{sequencePoint.StartColumn} -> {sequencePoint.EndLine},{sequencePoint.EndColumn}");
	}
}
