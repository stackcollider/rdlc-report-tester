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

        // Проход 1: собрать все сырые значения и атрибуты по таблицам
        // rawData[tableName] = list of rows, row = list of (name, decimalformatter, value)
        var rawData = new Dictionary<string, (List<(string name, bool hasDecimalFmt)> schema, List<List<string>> rows)>(StringComparer.Ordinal);

        foreach (XElement dataItem in dataItemsRoot.Elements("DataItem"))
        {
            string tableName = dataItem.Attribute("name")?.Value
                ?? throw new InvalidDataException("<DataItem> is missing the 'name' attribute.");

            if (!rawData.ContainsKey(tableName))
                rawData[tableName] = ([], []);

            var (schema, rows) = rawData[tableName];

            foreach (XElement columnsBlock in dataItem.Elements("Columns"))
            {
                List<XElement> cols = [.. columnsBlock.Elements("Column")];

                if (schema.Count == 0)
                    foreach (XElement col in cols)
                    {
                        string name = col.Attribute("name")?.Value
                            ?? throw new InvalidDataException("<Column> is missing the 'name' attribute.");
                        schema.Add((name, col.Attribute("decimalformatter") != null));
                    }

                rows.Add(cols.Select(c => c.Value).ToList());
            }
        }

        // Проход 2: определить тип каждой колонки и создать DataTable
        var tables = new List<DataTable>();

        foreach (var (tableName, (schema, rows)) in rawData)
        {
            DataTable table = new(tableName);

            // Определяем тип колонки по всем строкам
            for (int ci = 0; ci < schema.Count; ci++)
            {
                var (colName, hasDecimalFmt) = schema[ci];
                Type colType = InferColumnType(rows, ci, hasDecimalFmt);
                table.Columns.Add(colName, colType);
            }

            // Заполняем строки
            foreach (List<string> rowValues in rows)
            {
                DataRow row = table.NewRow();
                for (int ci = 0; ci < schema.Count && ci < rowValues.Count; ci++)
                {
                    string colName = schema[ci].name;
                    row[colName] = ConvertValue(rowValues[ci], table.Columns[colName]!.DataType);
                }
                table.Rows.Add(row);
            }

            tables.Add(table);
        }

        return tables;
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
