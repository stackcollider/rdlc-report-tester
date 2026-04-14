using System.Data;
using System.IO;
using System.Xml.Linq;
using Microsoft.Reporting.NETCore;

namespace RdlcRendererWpf;

public static class ReportRenderer
{
    public static void Render(string rdlcPath, string xmlPath, string outputPath)
    {
        List<DataTable> tables = ParseDataItems(xmlPath);

        using LocalReport report = new();
        using FileStream rdlcStream = File.OpenRead(rdlcPath);
        report.LoadReportDefinition(rdlcStream);

        List<string> dsNames = GetRdlcDataSetNames(rdlcPath);
        for (int i = 0; i < tables.Count; i++)
        {
            string dsName = i < dsNames.Count ? dsNames[i] : tables[i].TableName;
            report.DataSources.Add(new ReportDataSource(dsName, tables[i]));
        }

        byte[] pdf = report.Render("PDF");

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(outputPath, pdf);
    }

    private static List<string> GetRdlcDataSetNames(string rdlcPath)
    {
        XDocument rdlc = XDocument.Load(rdlcPath);
        XNamespace ns = rdlc.Root?.Name.Namespace ?? XNamespace.None;
        return rdlc.Descendants(ns + "DataSet")
                   .Select(e => e.Attribute("Name")?.Value ?? "")
                   .Where(n => !string.IsNullOrEmpty(n))
                   .ToList();
    }

    private static List<DataTable> ParseDataItems(string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);

        XElement? dataItemsRoot =
            doc.Root?.Name.LocalName == "DataItems"
                ? doc.Root
                : doc.Root?.Element("DataItems");

        if (dataItemsRoot == null)
            throw new InvalidDataException("Cannot find <DataItems> element in XML.");

        // Рекурсивно собираем денормализованные строки: каждая листовая строка
        // наследует значения колонок родительских DataItem-ов.
        var denormRows = new List<List<(string name, bool hasDecimalFmt, string value)>>();
        CollectDenormalizedRows(dataItemsRoot, [], denormRows);

        if (denormRows.Count == 0)
            return [];

        // Определяем уникальные колонки (в порядке первого появления)
        var colOrder   = new List<string>();
        var colDecFmt  = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in denormRows)
            foreach (var (name, hasDecimalFmt, _) in row)
                if (!colDecFmt.ContainsKey(name))
                {
                    colDecFmt[name] = hasDecimalFmt;
                    colOrder.Add(name);
                }

        // Определяем типы по всем значениям каждой колонки
        var colTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (string colName in colOrder)
        {
            var singleColRows = denormRows
                .Select(r => new List<string> { r.FirstOrDefault(c => c.name == colName).value ?? "" })
                .ToList();
            colTypes[colName] = InferColumnType(singleColRows, 0, colDecFmt[colName]);
        }

        // Создаём одну плоскую DataTable
        DataTable table = new("DataSet_Result");
        foreach (string colName in colOrder)
            table.Columns.Add(colName, colTypes[colName]);

        foreach (var rowData in denormRows)
        {
            DataRow row = table.NewRow();
            foreach (var (name, _, value) in rowData)
                if (table.Columns.Contains(name))
                    row[name] = ConvertValue(value, colTypes[name]);
            table.Rows.Add(row);
        }

        return [table];
    }

    internal static void CollectDenormalizedRows(
        XElement dataItemsEl,
        List<(string name, bool hasDecimalFmt, string value)> inherited,
        List<List<(string name, bool hasDecimalFmt, string value)>> result)
    {
        foreach (XElement di in dataItemsEl.Elements("DataItem"))
        {
            var ownCols = (di.Element("Columns")?.Elements("Column") ?? [])
                .Select(c => (
                    c.Attribute("name")?.Value ?? "?",
                    c.Attribute("decimalformatter") != null,
                    c.Value))
                .ToList();

            var combined = new List<(string, bool, string)>([..inherited, ..ownCols]);

            XElement? nested = di.Element("DataItems");
            if (nested != null && nested.Elements("DataItem").Any())
                CollectDenormalizedRows(nested, combined, result);
            else
                result.Add(combined);
        }
    }

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd",
        "MM/dd/yyyy", "MM-dd-yyyy",
        "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"
    ];

    private static bool TryParseDate(string value, out DateTime result) =>
        DateTime.TryParseExact(value, DateFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out result);

    internal static string InferTypeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "null";
        var t = InferColumnType([[value]], 0, false);
        if (t == typeof(int))      return "int";
        if (t == typeof(decimal))  return "decimal";
        if (t == typeof(DateTime)) return "datetime";
        return "string";
    }

    // Определяет тип колонки: int → decimal → DateTime → string
    internal static Type InferColumnType(List<List<string>> rows, int colIndex, bool hasDecimalFmt)
    {
        if (hasDecimalFmt) return typeof(decimal);

        bool allInt      = true;
        bool allDecimal  = true;
        bool allDateTime = true;

        foreach (List<string> row in rows)
        {
            string v = colIndex < row.Count ? row[colIndex] : "";
            if (string.IsNullOrWhiteSpace(v)) continue; // пустые не влияют на тип

            if (allInt && !int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                allInt = false;

            if (allDecimal && !decimal.TryParse(v,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                allDecimal = false;

            if (allDateTime && !TryParseDate(v, out _))
                allDateTime = false;

            if (!allDecimal && !allDateTime) break;
        }

        if (allInt)      return typeof(int);
        if (allDecimal)  return typeof(decimal);
        if (allDateTime) return typeof(DateTime);
        return typeof(string);
    }

    private static object ConvertValue(string value, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(value)) return DBNull.Value;

        if (targetType == typeof(int))
            return int.TryParse(value, out int i) ? i : DBNull.Value;

        if (targetType == typeof(decimal))
            return decimal.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal d) ? d : DBNull.Value;

        if (targetType == typeof(DateTime))
            return TryParseDate(value, out DateTime dt) ? dt : DBNull.Value;

        return value;
    }
}
