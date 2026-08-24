using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeskPet.Core.Chat;
using DeskPet.Core.RAG;

namespace DeskPet.Windows.Views;

public partial class SettingsWindow : Window
{
    private bool _loading = true;
    private bool _testingConnection;
    private bool _importing;

    public SettingsWindow()
    {
        InitializeComponent();
        LoadValues();
        _loading = false;
        RefreshDocuments();
        SettingsNav.SelectedIndex = 0;
    }

    /// <summary>设置页展示条目（RAGDocument 的快照，ImportedAt 转本地时间）。</summary>
    private sealed class DocumentItem
    {
        public required RAGDocument Document { get; init; }

        public string SourceName { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;
    }

    private void LoadValues()
    {
        ChatEnabledCheck.IsChecked = AppServices.Settings.ChatEnabled;
        ChatBaseUrlBox.Text = AppServices.Settings.ChatBaseUrl;
        ChatApiKeyBox.Password = AppServices.Settings.ChatApiKey;
        ChatModelBox.Text = AppServices.Settings.ChatModel;
        ChatSystemPromptBox.Text = AppServices.Settings.ChatSystemPrompt;
        RagEnabledCheck.IsChecked = AppServices.Settings.RagEnabled;
        EmbeddingModelBox.Text = AppServices.Settings.EmbeddingModel;
        UpdateTestButtonState();
    }

    private void OnSettingsNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SectionTitle.Text = SettingsNav.SelectedItem is ListBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : string.Empty;

        var index = SettingsNav.SelectedIndex;
        PetPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        RagPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 2) RefreshDocuments();
    }

    // MARK: - Chat 页（写穿 ISettingsService，语义对应 Swift @Default）

    private void OnChatEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ChatEnabled = ChatEnabledCheck.IsChecked == true;
        UpdateTestButtonState();
    }

    private void OnChatBaseUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ChatBaseUrl = ChatBaseUrlBox.Text;
    }

    private void OnChatApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ChatApiKey = ChatApiKeyBox.Password;
    }

    private void OnChatModelLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ChatModel = ChatModelBox.Text;
        UpdateTestButtonState();
    }

    private void OnChatSystemPromptLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ChatSystemPrompt = ChatSystemPromptBox.Text;
    }

    /// <summary>测试连接（Swift ChatSettings.test）：运行中禁用按钮，结果内联展示，不阻塞 UI 线程。</summary>
    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        _testingConnection = true;
        UpdateTestButtonState();
        ChatTestStatus.Foreground = System.Windows.Media.Brushes.Gray;
        ChatTestStatus.Text = "测试中…";
        try
        {
            await AppServices.Chat.TestConnectionAsync(CancellationToken.None);
            ChatTestStatus.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ChatTestStatus.Text = "连接成功，模型已响应。";
        }
        catch (PetChatException ex)
        {
            ChatTestStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            ChatTestStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            ChatTestStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            ChatTestStatus.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            _testingConnection = false;
            UpdateTestButtonState();
        }
    }

    /// <summary>Swift 语义：未启用对话或模型名为空时禁用测试按钮；运行中亦禁用。</summary>
    private void UpdateTestButtonState()
    {
        ChatTestButton.IsEnabled =
            !_testingConnection
            && ChatEnabledCheck.IsChecked == true
            && !string.IsNullOrWhiteSpace(ChatModelBox.Text);
    }

    // MARK: - RAG 页（设置）

    private void OnRagEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.RagEnabled = RagEnabledCheck.IsChecked == true;
    }

    private void OnEmbeddingModelLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.EmbeddingModel = EmbeddingModelBox.Text;
    }

    /// <summary>选择并导入文档（多选 .txt/.md/.markdown）：逐个异步导入，进度/结果内联展示。</summary>
    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要导入的文档",
            Filter = "文本文件 (*.txt;*.md;*.markdown)|*.txt;*.md;*.markdown|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var files = dialog.FileNames;
        _importing = true;
        UpdateImportState();

        var results = new List<string>();
        for (var i = 0; i < files.Length; i++)
        {
            var path = files[i];
            var name = Path.GetFileName(path);
            RagStatus.Foreground = System.Windows.Media.Brushes.Gray;
            RagStatus.Text = $"正在导入 {name}（{i + 1}/{files.Length}）…";
            try
            {
                await AppServices.Rag.ImportAsync(path, name);
                results.Add($"导入成功：{name}");
            }
            catch (Exception ex)
            {
                results.Add($"导入失败：{name} — {ex.Message}");
            }
        }

        RagStatus.Foreground = System.Windows.Media.Brushes.Gray;
        RagStatus.Text = string.Join("\n", results);
        RefreshDocuments();
        _importing = false;
        UpdateImportState();
    }

    private async void OnRemoveDocumentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: DocumentItem item }) return;
        try
        {
            await AppServices.Rag.RemoveAsync(item.Document.Id);
            RagStatus.Foreground = System.Windows.Media.Brushes.Gray;
            RagStatus.Text = $"已删除：{item.SourceName}";
        }
        catch (Exception ex)
        {
            RagStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            RagStatus.Text = "删除失败：" + ex.Message;
        }
        RefreshDocuments();
    }

    private async void OnClearDocumentsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await AppServices.Rag.ClearAsync();
            RagStatus.Foreground = System.Windows.Media.Brushes.Gray;
            RagStatus.Text = "已清空文档库。";
        }
        catch (Exception ex)
        {
            RagStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            RagStatus.Text = "清空失败：" + ex.Message;
        }
        RefreshDocuments();
    }

    private void RefreshDocuments()
    {
        var items = AppServices.Rag.Documents
            .OrderByDescending(d => d.ImportedAt)
            .Select(d => new DocumentItem
            {
                Document = d,
                SourceName = d.SourceName,
                Detail = $"{d.ChunkCount} 块 · {d.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            })
            .ToList();

        DocList.ItemsSource = items;
        NoDocsHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateImportState();
    }

    /// <summary>导入进行中禁用导入/删除/清空（Swift isImporting 语义）。</summary>
    private void UpdateImportState()
    {
        ImportButton.IsEnabled = !_importing;
        DocList.IsEnabled = !_importing;
        ClearDocsButton.IsEnabled = !_importing && DocList.Items.Count > 0;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
    }
}
