using System.Windows;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class QualityWindow : Window
{
    public QualityWindow(QualityReport report)
    {
        InitializeComponent();
        IssuesGrid.ItemsSource = report.Issues.OrderByDescending(issue => issue.Severity).ThenBy(issue => issue.UnitId);
        SummaryText.Text = $"{report.CheckedUnits:N0} aktív sor · {report.ErrorCount:N0} hiba · {report.WarningCount:N0} figyelmeztetés" +
            (report.IsSafe ? " · nincs exportot blokkoló determinisztikus hiba" : " · a hibás kész sorok Ellenőrzendő állapotba kerültek");
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
