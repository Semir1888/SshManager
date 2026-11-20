using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SshManager.App.Views;

/// <summary>
/// 防火墙规则表单对话框（添加 / 修改共用）。
/// 修改 = 删除原编号规则 + 按新配置添加（由 NetworkViewModel 组合执行）。
/// </summary>
public partial class UfwRuleDialog : Window
{
    /// <summary>确定后的结果字段（动作 / 方向 / 端口 / 协议 / 来源）。</summary>
    public string Action { get; private set; } = "允许";
    public bool Outbound { get; private set; }
    public string Port { get; private set; } = "";
    public string Proto { get; private set; } = "任意";
    public string From { get; private set; } = "";

    public UfwRuleDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(false);
        }, RoutingStrategies.Tunnel);

        void Watch(Avalonia.Controls.Primitives.SelectingItemsControl? box)
        {
            if (box == null) return;
            box.SelectionChanged += (_, _) => UpdatePreview();
        }
        Watch(ActionBox);
        Watch(DirectionBox);
        Watch(ProtoBox);
        PortBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        FromBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
    }

    /// <summary>
    /// 预填表单。action 允许/拒绝/限制；proto 任意/TCP/UDP。
    /// </summary>
    public UfwRuleDialog(bool editMode, string action, bool outbound, string port, string proto, string from)
        : this()
    {
        Title = editMode ? "修改防火墙规则" : "添加防火墙规则";
        TitleText.Text = Title;
        SubText.Text = editMode
            ? "保存后先删除原编号规则，再按新配置添加"
            : "按表单生成 ufw 命令并执行";
        OkButton.Content = editMode ? "保存修改" : "添加规则";
        ActionBox.SelectedIndex = action switch { "拒绝" => 1, "限制" => 2, _ => 0 };
        DirectionBox.SelectedIndex = outbound ? 1 : 0;
        ProtoBox.SelectedIndex = proto switch { "TCP" => 1, "UDP" => 2, _ => 0 };
        PortBox.Text = port;
        FromBox.Text = from;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            var action = ActionBox.SelectedIndex switch { 1 => "deny", 2 => "limit", _ => "allow" };
            var dir = DirectionBox.SelectedIndex == 1 ? " out" : "";
            var proto = ProtoBox.SelectedIndex switch { 1 => "tcp", 2 => "udp", _ => "" };
            var port = (PortBox.Text ?? "").Trim();
            var from = (FromBox.Text ?? "").Trim();
            var sb = new System.Text.StringBuilder($"ufw {action}{dir}");
            if (from.Length > 0)
            {
                sb.Append(" from ").Append(from);
                if (port.Length > 0) sb.Append(" to any port ").Append(port);
                if (proto.Length > 0) sb.Append(" proto ").Append(proto);
            }
            else
            {
                if (port.Length == 0) { PreviewText.Text = "（不限来源时必须指定端口）"; return; }
                sb.Append(' ').Append(port);
                if (proto.Length > 0) sb.Append('/').Append(proto);
            }
            PreviewText.Text = "将执行：" + sb;
            ErrorText.Text = "";
        }
        catch { }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var port = (PortBox.Text ?? "").Trim();
        var from = (FromBox.Text ?? "").Trim();
        if (port.Length == 0 && from.Length == 0)
        {
            ErrorText.Text = "请至少填写端口或来源之一。";
            return;
        }
        if (port.Length == 0 && from.Length > 0 && DirectionBox.SelectedIndex != 1)
        {
            // 入站 + 只有来源 = 对该来源全部端口放行，ufw 支持（ufw allow from X），放行
        }
        Action = ActionBox.SelectedIndex switch { 1 => "拒绝", 2 => "限制", _ => "允许" };
        Outbound = DirectionBox.SelectedIndex == 1;
        Proto = ProtoBox.SelectedIndex switch { 1 => "TCP", 2 => "UDP", _ => "任意" };
        Port = port;
        From = from;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
