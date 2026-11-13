using System.IO;
using System.Windows;
using ColdHam.Core;

namespace ColdHam.App;

public partial class RuleEditorWindow : Window
{
    private readonly RuleKind _kind;
    private readonly string[] _rules;
    private readonly int _index;
    public string[]? EditedRules { get; private set; }

    public RuleEditorWindow(RuleKind kind, string[] rules, int index)
    {
        InitializeComponent();
        _kind = kind;
        _rules = rules;
        _index = index;
        Prompt.Text = LocalizedText.Get(kind == RuleKind.Application ? "RuleEditApplicationPrompt" : "RuleEditWebsitePrompt");
        Subdomains.Visibility = kind == RuleKind.AllowedWebsite ? Visibility.Visible : Visibility.Collapsed;
        if (kind == RuleKind.AllowedWebsite)
        {
            var rule = AllowedWebsiteRule.Parse(rules[index]);
            ValueInput.Text = rule.Domain;
            Subdomains.IsChecked = rule.IncludeSubdomains;
        }
        else ValueInput.Text = rules[index];
        Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EditedRules = RuleEditing.Replace(_rules, _index, ValueInput.Text, _kind, Subdomains.IsChecked == true);
            DialogResult = true;
        }
        catch (InvalidDataException error) { ErrorText.Text = error.Message; }
    }
}
