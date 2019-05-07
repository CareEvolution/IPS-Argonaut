#r "Newtonsoft.Json.dll"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

enum Use
{
    Prohibited,
    Optional,
    MustSupport,
    Required,
}

class Element
{
    public Element(JToken element, JToken baseElement)
    {
        var path = (string)element["path"];
        var pathSegments = path.Split('.');
        Path = string.Join(".", pathSegments.Skip(1));
        var typeObject = element["type"]?[0] ?? baseElement?["type"]?[0];
        var type = (string)typeObject?["code"];
        var profile = (string)typeObject?["profile"];
        if (type == "Extension")
        {
            Key = string.IsNullOrEmpty(profile) ?
                Path :
                $"{Path}[{profile}]";
            Type = type;
        }
        else
        {
            Key = Path;
            if (string.IsNullOrEmpty(profile))
            {
                Type = type;
            }
            else
            {
                Type = profile.Split('/').Last();
            }
        }
        var min = Convert.ToInt32(element["min"] ?? baseElement?["min"] ?? 0);
        if (min != 0 && min != 1)
        {
            throw new InvalidOperationException($"{Key}: min value '{min}' is not supported");
        }
        var max = (string)(element["max"] ?? baseElement?["max"] ?? "*");
        if (max != "0" && max != "1" && max != "*")
        {
            throw new InvalidOperationException($"{Key}: max value '{max}' is not supported");
        }
        var mustSupport = (bool)(element["mustSupport"] ?? baseElement?["mustSupport"] ?? false);
        if (max == "0")
        {
            Use = Use.Prohibited;
        }
        else if (min == 1)
        {
            Use = Use.Required;
        }
        else if (mustSupport)
        {
            Use = Use.MustSupport;
        }
        else
        {
            Use = Use.Optional;
        }
        Multiple = max == "*";
        var baseBinding = baseElement?["binding"];
        var binding = element["binding"] ?? baseBinding;
        if (binding != null)
        {
            var strength = (string)(binding["strength"] ?? baseBinding?["strength"]);
            var valueSetUri = (string)(binding["valueSetReference"]?["reference"] ?? binding["valueSetUri"] ?? baseBinding?["valueSetReference"]?["reference"] ?? baseBinding?["valueSetUri"]);
            var valueSetName = valueSetUri?.Split('/').Last();
            Binding = $"{strength}: {valueSetName}";
        }
    }

    public string Key { get; }
    public string Path { get; }
    public string Type { get; }
    public Use Use { get; }
    bool Multiple { get; }
    public string Binding { get; }

    public void Dump()
    {
        Console.WriteLine(Key);
        Console.WriteLine("    {0}{1}", Use, Multiple ? " *" : string.Empty );
        if (!string.IsNullOrEmpty(Type))
        {
            Console.WriteLine("    {0}", Type);
        }
        if (!string.IsNullOrEmpty(Binding))
        {
            Console.WriteLine("    {0}", Binding);
        }
    }

    public static List<ElementCompareResult> Compare(Element left, Element right)
    {
        var result = new List<ElementCompareResult>();
        if (left.Type != right.Type)
        {
            result.Add(new ElementCompareResult("Type", left.Type, right.Type));
        }
        if (left.Use != right.Use)
        {
            result.Add(new ElementCompareResult("Use", left.Use, right.Use));
        }
        if (left.Multiple != right.Multiple)
        {
            result.Add(new ElementCompareResult("Multiple", left.Multiple ? "Many" : "One", right.Multiple ? "Many" : "One"));
        }
        if (left.Binding != right.Binding)
        {
            result.Add(new ElementCompareResult("Binding", left.Binding, right.Binding));
        }
        if (result.Count == 0)
        {
            return null;
        }
        return result;
    }
}

class ElementCompareResult
{
    public ElementCompareResult( string name, object left, object right )
    {
        Name = name;
        Left = left;
        Right = right;
    }

    public string Name { get; }
    public object Left { get; }
    public object Right { get; }
}

class StructureDefinition
{
    public StructureDefinition(string filePath) :
        this(JObject.Parse(File.ReadAllText(filePath)))
    {}

    public StructureDefinition(JObject json)
    {
        var snapshotElements = json["snapshot"]["element"];
        Snapshot = ToElements(snapshotElements, null);
        Differential = ToElements(json["differential"]["element"], snapshotElements);
    }

    public IEnumerable<Element> Snapshot { get; }

    public IEnumerable<Element> Differential { get; }

    private static IEnumerable<Element> ToElements(JToken elements, JToken baseElements)
    {
        var result = new List<Element>();
        foreach (var element in elements)
        {
            var path = (string)element["path"];
            var baseElement = baseElements?.FirstOrDefault(be => (string)be["path"] == path);
            result.Add(new Element(element, baseElement));
        }
        return result;
    }
}

class DataTypes
{
    public DataTypes(string filePath)
    {
        var jsonString = File.ReadAllText(filePath);
        var dataTypesJson = JObject.Parse(jsonString);
        foreach (var entry in dataTypesJson["entry"])
        {
            var resource = entry["resource"];
            if (resource != null && (string)resource["resourceType"] == "StructureDefinition")
            {
                var id = (string)resource["id"];
                _dataTypes[id] = new StructureDefinition((JObject)resource);
            }
        }
    }

    public Element GetDataTypeElement(string path, StructureDefinition structureDefinition)
    {
        var pathSegments = path.Split('.');
        for (var i = pathSegments.Length - 1; i > 0; i--)
        {
            var basePath = string.Join(".", pathSegments.Take(i));
            var baseElement = structureDefinition.Snapshot.FirstOrDefault(e => e.Path == basePath);
            if (baseElement != null)
            {
                var dataType = GetDataType(baseElement.Type);
                if (dataType != null)
                {
                    var dataTypePath = string.Join(".", pathSegments.Skip(i));
                    return dataType.Snapshot.FirstOrDefault(e => e.Path == dataTypePath);
                }
            }
        }
        return null;
    }

    public StructureDefinition GetDataType(string id)
    {
        if (_dataTypes.TryGetValue(id, out var result))
        {
            return result;
        }
        return null;
    }

    private readonly Dictionary<string, StructureDefinition> _dataTypes = new Dictionary<string, StructureDefinition>();
}

void Dump(string key, List<ElementCompareResult> difference)
{
    if (difference != null)
    {
        Console.WriteLine(key);
        foreach (var item in difference)
        {
            Console.WriteLine("    {1} - {2}", item.Name, item.Left, item.Right);
        }
    }
}

void Dump(Element element, bool onlyLeft)
{
    Console.WriteLine(element.Key);
    if (onlyLeft)
    {
        Console.WriteLine("    {0} - N/A", element.Type);
    }
    else
    {
        Console.WriteLine("    N/A - {0}", element.Type);
    }
}

string ComputeLabel(string key, string previousKey)
{
    if (previousKey == null)
    {
        return key;
    }
    var previousSegments = previousKey.Split('.');
    var segments = key.Split('.');
    var spaces = string.Empty;
    var i = 0;
    while (i<previousSegments.Length && i<segments.Length && previousSegments[i] == segments[i])
    {
        spaces += "    ";
        i++;
    }
    if (i == 0)
    {
        return key;
    }
    return spaces + "." + string.Join(".", segments.Skip(i));
}

bool DumpCSV(TextWriter writer, string label, List<ElementCompareResult> difference)
{
    if (difference == null)
    {
        return false;
    }
    var first = true;
    foreach (var item in difference)
    {
        if (first)
        {
            writer.Write(label);
            first = false;
        }
        writer.WriteLine(",{0},{1}", item.Left, item.Right);
    }
    return true;
}

void DumpCSV(TextWriter writer, string label, Use use, bool onlyLeft)
{
    writer.Write(label);
    if (onlyLeft)
    {
        writer.WriteLine(",{0},N/A", use);
    }
    else
    {
        writer.WriteLine(",N/A,{0}", use);
    }
}

if (Args.Count != 4)
{
    Console.WriteLine("Usage: csi Compare.csx <version directory> <IPS file> <Argonaut file> <output file>");
    return;
}

var dataTypesFilePath = Path.Combine(Args[0], "profiles-types.json");

var leftFilePath = Args[1];
var rightFilePath = Args[2];
var csvFilePath = Args[3];

var left = new StructureDefinition(leftFilePath);
var right = new StructureDefinition(rightFilePath);

var dataTypes = new DataTypes(dataTypesFilePath);

Console.WriteLine("Creating '{0}'", csvFilePath);
using (var writer = new StreamWriter(csvFilePath))
{
    string previousKey = null;
    foreach (var leftElement in left.Differential)
    {
        var rightElement = right.Differential.FirstOrDefault(e => e.Key == leftElement.Key);
        if (rightElement == null)
        {
            rightElement = right.Snapshot.FirstOrDefault(e => e.Key == leftElement.Key);
        }
        if (rightElement == null)
        {
            rightElement = dataTypes.GetDataTypeElement(leftElement.Path, right);
        }
        var label = ComputeLabel(leftElement.Key, previousKey);
        if (rightElement == null)
        {
            DumpCSV(writer, label, leftElement.Use, onlyLeft: true);
            previousKey = leftElement.Key;
        }
        else
        {
            var difference = Element.Compare(leftElement, rightElement);
            if (DumpCSV(writer, label, difference))
            {
                previousKey = leftElement.Key;
            }
        }
    }
    previousKey = null;
    foreach (var rightElement in right.Differential)
    {
        var leftElement = left.Differential.FirstOrDefault(e => e.Key == rightElement.Key);
        if (leftElement == null)
        {
            leftElement = left.Snapshot.FirstOrDefault(e => e.Key == rightElement.Key);
            if (leftElement == null)
            {
                leftElement = dataTypes.GetDataTypeElement(rightElement.Path, left);
            }
            var label = ComputeLabel(rightElement.Key, previousKey);
            if (leftElement == null)
            {
                DumpCSV(writer, label, rightElement.Use, onlyLeft: false);
                previousKey = rightElement.Key;
            }
            else
            {
                var difference = Element.Compare(leftElement, rightElement);
                if (DumpCSV(writer, label, difference))
                {
                    previousKey = rightElement.Key;
                }
            }
        }
    }
}
