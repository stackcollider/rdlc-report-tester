using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace RdlcRendererWpf;

public partial class MainWindow : Window
{
    private bool _webViewReady = false;
    private DataTable? _currentXmlDataTable;

    public MainWindow()
    {
        InitializeComponent();
        InitWebView();
    }

    private async void InitWebView()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RdlcReportTester", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await PdfViewer.EnsureCoreWebView2Async(env);
            _webViewReady = true;
        }
        catch
        {
            // WebView2 runtime не установлен — предпросмотр недоступен
        }
    }

    private void UpdatePathLabels()
    {
        RdlcLabel.Text = string.IsNullOrWhiteSpace(RdlcPathBox.Text)
            ? "RDLC File"
            : $"RDLC File : {Path.GetFileName(RdlcPathBox.Text)}";

        XmlLabel.Text = string.IsNullOrWhiteSpace(XmlPathBox.Text)
            ? "XML Data (BC Format)"
            : $"XML Data (BC Format) : {Path.GetFileName(XmlPathBox.Text)}";

        OutputLabel.Text = string.IsNullOrWhiteSpace(OutputPathBox.Text)
            ? "Save PDF As"
            : $"Save PDF As : {Path.GetFileName(OutputPathBox.Text)}";
    }

    private void LoadXmlDataToGrid(string xmlPath)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
        {
            XmlDataGrid.ItemsSource = null;
            return;
        }

        try
        {
            var doc = XDocument.Load(xmlPath);

            if (TryLoadXmlAsRowTable(doc))
                return;

            if (TryLoadXmlAsPivotColumnTable(doc))
                return;

            var leaves = doc.Descendants().Where(e => !e.Elements().Any());

            var table = new DataTable();
            table.Columns.Add("XPath", typeof(string));
            table.Columns.Add("Value", typeof(string));
            table.Columns.Add("Data Type", typeof(string));

            foreach (var leaf in leaves)
            {
                string xpath = GetXPath(leaf);
                string value = leaf.Value;
                string type = GetXmlTypeName(leaf);

                table.Rows.Add(xpath, value, type);
            }

            XmlDataGrid.ItemsSource = table.DefaultView;
            XmlDataGrid.SelectedIndex = 0;
            _currentXmlDataTable = table;
            UpdateXmlDataFieldGrid(0);
        }
        catch (Exception ex)
        {
            XmlDataGrid.ItemsSource = null;
            SetStatus($"Ошибка загрузки XML: {ex.Message}", isError: true);
        }
    }

    private static string GetXmlTypeName(XElement element)
    {
        var xsiType = element.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value;
        if (!string.IsNullOrWhiteSpace(xsiType))
            return xsiType;

        return ReportRenderer.InferTypeName(element.Value);
    }

    private static string GetXPath(XElement element)
    {
        return "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(e => e.Name.LocalName));
    }

    private void UpdateXmlDataFieldGrid(int rowIndex)
    {
        if (_currentXmlDataTable == null || _currentXmlDataTable.Rows.Count == 0)
        {
            XmlDataFieldGrid.ItemsSource = null;
            return;
        }

        if (rowIndex < 0 || rowIndex >= _currentXmlDataTable.Rows.Count)
            rowIndex = 0;

        var row = _currentXmlDataTable.Rows[rowIndex];

        var list = new List<FieldTypeValueItem>();

        foreach (DataColumn col in _currentXmlDataTable.Columns)
        {
            string columnName = col.ColumnName;
            string fieldName = columnName;
            string typeName = col.DataType.Name;

            // Column names are stored as "FieldName (type)" — extract both parts
            int parenStart = columnName.LastIndexOf(" (");
            if (parenStart >= 0 && columnName.EndsWith(")"))
            {
                fieldName = columnName[..parenStart];
                typeName  = columnName[(parenStart + 2)..^1];
            }

            list.Add(new FieldTypeValueItem
            {
                Field = fieldName,
                Type  = typeName,
                Value = row[col]?.ToString() ?? string.Empty
            });
        }

        XmlDataFieldGrid.ItemsSource = list;
    }

    private void XmlDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_currentXmlDataTable == null) return;

        if (XmlDataGrid.SelectedIndex >= 0 && XmlDataGrid.SelectedIndex < _currentXmlDataTable.Rows.Count)
            UpdateXmlDataFieldGrid(XmlDataGrid.SelectedIndex);
    }

    private void XmlDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only act if the click was on a DataGridRow, not a header or empty area
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.DataGridRow && hit is not System.Windows.Controls.DataGrid)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);

        if (hit is not System.Windows.Controls.DataGridRow)
            return;

        // SelectionChanged already updated XmlDataFieldGrid — just switch the tab
        MainTabControl.SelectedIndex = 2;
        e.Handled = true;
    }

    private class FieldTypeValueItem
    {
        public string Field { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private bool TryLoadXmlAsRowTable(XDocument doc)
    {
        var dataItems = doc.Descendants("DataItem").ToList();
        if (!dataItems.Any())
            return false;

        var rows = dataItems
            .Select(di => di.Element("Columns")?.Elements("Column").ToList())
            .Where(c => c != null && c.Any())
            .ToList();

        if (!rows.Any())
            return false;

        var maxColumns = rows.Max(r => r.Count);

        // Headers are Column name attributes (preferred)
        var firstRow = rows.First();
        var headerNames = Enumerable.Range(0, maxColumns)
            .Select(i =>
            {
                var col = i < firstRow.Count ? firstRow[i] : null;
                return col?.Attribute("name")?.Value ?? $"Column {i + 1}";
            })
            .ToList();

        // If any header is missing and firstRow has values that look like names (non-numeric / non-date), use them
        for (int i = 0; i < maxColumns; i++)
        {
            if (string.IsNullOrWhiteSpace(headerNames[i]) && i < firstRow.Count)
            {
                var candidate = firstRow[i].Value?.Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !TryParseValue(candidate))
                    headerNames[i] = candidate;
            }
        }

        // Determine type: prefer explicit attribute, then first non-empty value across all rows
        var types = Enumerable.Range(0, maxColumns)
            .Select(i =>
            {
                foreach (var r in rows)
                {
                    if (i >= r.Count) continue;
                    var typAttr = r[i].Attribute("datatype")?.Value ?? r[i].Attribute("decimalformatter")?.Value;
                    if (!string.IsNullOrWhiteSpace(typAttr))
                        return typAttr;
                }

                foreach (var r in rows)
                {
                    if (i >= r.Count) continue;
                    var value = r[i].Value;
                    if (!string.IsNullOrWhiteSpace(value))
                        return ReportRenderer.InferTypeName(value);
                }

                return "null";
            })
            .ToList();

        var table = new DataTable();
        for (int i = 0; i < maxColumns; i++)
            table.Columns.Add($"{headerNames[i]} ({types[i]})", typeof(string));

        foreach (var rowColumns in rows)
        {
            var row = table.NewRow();
            for (int i = 0; i < maxColumns; i++)
                row[i] = i < rowColumns.Count ? rowColumns[i].Value : string.Empty;
            table.Rows.Add(row);
        }

        XmlDataGrid.ItemsSource = table.DefaultView;
        XmlDataGrid.SelectedIndex = 0;
        _currentXmlDataTable = table;
        UpdateXmlDataFieldGrid(0);
        return true;
    }

    private static bool TryParseValue(string value)
    {
        if (bool.TryParse(value, out _)) return true;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return true;
        return false;
    }

    private bool TryLoadXmlAsPivotColumnTable(XDocument doc)
    {
        var deepGroup = doc.Descendants()
            .Where(e => e.Elements().Any() && e.Elements().All(x => !x.Elements().Any()))
            .OrderByDescending(e => e.Elements().Count())
            .FirstOrDefault(e => e.Elements().GroupBy(x => x.Name).Any(g => g.Count() > 1));

        if (deepGroup == null)
            return false;

        var entries = deepGroup.Elements().ToList();
        if (!entries.Any())
            return false;

        var table = new DataTable();
        for (int i = 0; i < entries.Count; i++)
        {
            var type = GetXmlTypeName(entries[i]);
            table.Columns.Add($"Column {i + 1} ({type})", typeof(string));
        }

        var row = table.NewRow();
        for (int i = 0; i < entries.Count; i++)
        {
            row[i] = entries[i].Value;
        }
        table.Rows.Add(row);

        XmlDataGrid.ItemsSource = table.DefaultView;
        XmlDataGrid.SelectedIndex = 0;
        _currentXmlDataTable = table;
        UpdateXmlDataFieldGrid(0);
        return true;
    }

    // --- Drag & Drop на уровне окна ---

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".rdlc")
                RdlcPathBox.Text = file;
            else if (ext == ".xml")
                XmlPathBox.Text = file;
        }

        AutoFillOutput();
        UpdatePathLabels();
        LoadXmlDataToGrid(XmlPathBox.Text);
        e.Handled = true;
    }

    // --- Browse ---

    private void BrowseRdlc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "RDLC files|*.rdlc", Title = "Выберите RDLC файл" };
        if (dlg.ShowDialog() == true)
        {
            RdlcPathBox.Text = dlg.FileName;
            AutoFillOutput();
            UpdatePathLabels();
        }
    }

    private void BrowseXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "XML files|*.xml", Title = "Выберите XML файл" };
        if (dlg.ShowDialog() == true)
        {
            XmlPathBox.Text = dlg.FileName;
            AutoFillOutput();
            UpdatePathLabels();
            LoadXmlDataToGrid(dlg.FileName);
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", FileName = "output.pdf" };
        if (dlg.ShowDialog() == true)
        {
            OutputPathBox.Text = dlg.FileName;
            UpdatePathLabels();
        }
    }

    private void AutoFillOutput()
    {
        if (!string.IsNullOrWhiteSpace(XmlPathBox.Text) && string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            string dir = Path.GetDirectoryName(XmlPathBox.Text) ?? "";
            string name = Path.GetFileNameWithoutExtension(XmlPathBox.Text);
            OutputPathBox.Text = Path.Combine(dir, name + ".pdf");
            UpdatePathLabels();
        }
    }

    // --- Render ---

    private async void RenderBtn_Click(object sender, RoutedEventArgs e)
    {
        string rdlcPath = RdlcPathBox.Text.Trim();
        string xmlPath = XmlPathBox.Text.Trim();
        string outputPath = OutputPathBox.Text.Trim();

        if (string.IsNullOrEmpty(rdlcPath) || string.IsNullOrEmpty(xmlPath) || string.IsNullOrEmpty(outputPath))
        {
            SetStatus("Заполните все три поля.", isError: true);
            return;
        }

        if (!File.Exists(rdlcPath)) { SetStatus($"Файл не найден: {rdlcPath}", isError: true); return; }
        if (!File.Exists(xmlPath)) { SetStatus($"Файл не найден: {xmlPath}", isError: true); return; }

        RenderBtn.IsEnabled = false;
        SetStatus("Создаю PDF...");

        try
        {
            await Task.Run(() => ReportRenderer.Render(rdlcPath, xmlPath, outputPath));
            SetStatus($"Готово — RDLC File : {Path.GetFileName(rdlcPath)} : XML File : {Path.GetFileName(xmlPath)} : Save PDF As : {Path.GetFileName(outputPath)}");
            ShowPdfPreview(outputPath);
        }
        catch (Exception ex)
        {
            string msg = ex.InnerException?.Message ?? ex.Message;
            SetStatus($"Ошибка: {msg}", isError: true);
        }
        finally
        {
            RenderBtn.IsEnabled = true;
        }
    }

    private void ShowPdfPreview(string pdfPath)
    {
        if (!_webViewReady) return;

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PdfViewer.Visibility = Visibility.Visible;
        PdfViewer.CoreWebView2.Navigate(new Uri(pdfPath).AbsoluteUri);
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? Brushes.Red : Brushes.Gray;
    }

    private void AboutBtn_Click(object sender, RoutedEventArgs e)
    {
        AboutWindow aboutWindow = new AboutWindow { Owner = this };
        aboutWindow.ShowDialog();
    }
}
