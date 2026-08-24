using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Monitor.Core.Alerts;

namespace Monitor.App.Controls;

/// <summary>
/// 340px 幅の縦長サイドバー向けの軽量な折りたたみセクション。
/// 標準の <see cref="Expander"/> より装飾を減らし、ヘッダー全体クリックで開閉する。
/// 開閉状態の反映（Visibility の切り替え・シェブロンの向き変更）はテンプレートのトリガーではなく
/// コードビハインドで直接行う。
/// </summary>
[TemplatePart(Name = PartHeader, Type = typeof(Border))]
[TemplatePart(Name = PartContent, Type = typeof(ContentPresenter))]
[TemplatePart(Name = PartSummary, Type = typeof(TextBlock))]
[TemplatePart(Name = PartSubtitle, Type = typeof(TextBlock))]
[TemplatePart(Name = PartChevron, Type = typeof(Path))]
[TemplatePart(Name = PartAccentBar, Type = typeof(Rectangle))]
public sealed class SectionExpander : HeaderedContentControl
{
    private const string PartHeader = "PART_Header";
    private const string PartContent = "PART_Content";
    private const string PartSummary = "PART_Summary";
    private const string PartSubtitle = "PART_Subtitle";
    private const string PartChevron = "PART_Chevron";
    private const string PartAccentBar = "PART_AccentBar";

    // 10x10 のビューポート内に描く三角形のシェブロン。フォント絵文字は使わない。
    private static readonly Geometry ChevronDownGeometry = CreateFrozenGeometry("M 2,3.5 L 8,3.5 L 5,8 Z");
    private static readonly Geometry ChevronRightGeometry = CreateFrozenGeometry("M 3.5,2 L 8,5 L 3.5,8 Z");

    private static readonly Brush DefaultAccentBrush = CreateFrozenSolid(Color.FromRgb(0x4E, 0xC9, 0xF5));

    public static readonly DependencyProperty SectionTitleProperty = DependencyProperty.Register(
        nameof(SectionTitle),
        typeof(string),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty SectionSummaryProperty = DependencyProperty.Register(
        nameof(SectionSummary),
        typeof(string),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty SectionSubtitleProperty = DependencyProperty.Register(
        nameof(SectionSubtitle),
        typeof(string),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnIsExpandedChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(DefaultAccentBrush));

    /// <summary>警告レベル。既定は <see cref="AlertLevel.None"/>（正常）。
    /// テンプレートの ControlTemplate.Triggers（BuildTemplate 内）が、この値に応じてアクセントバー
    /// （<see cref="PartAccentBar"/>）の色と、折りたたみ時の <see cref="SectionSummary"/> の文字色を
    /// 警告色へ切り替える。既存要素の色を変えるだけなので高さは1pxも増えない。</summary>
    public static readonly DependencyProperty AlertLevelProperty = DependencyProperty.Register(
        nameof(AlertLevel),
        typeof(AlertLevel),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(AlertLevel.None));

    public static readonly DependencyProperty SectionKeyProperty = DependencyProperty.Register(
        nameof(SectionKey),
        typeof(string),
        typeof(SectionExpander),
        new FrameworkPropertyMetadata(string.Empty));

    // DP 登録がすべて完了した後で構築する（BuildTemplate は上記の DependencyProperty を参照するため）。
    private static readonly ControlTemplate SharedTemplate = BuildTemplate();

    private Border? _headerPart;
    private ContentPresenter? _contentPart;
    private TextBlock? _summaryPart;
    private TextBlock? _subtitlePart;
    private Path? _chevronPart;

    public SectionExpander()
    {
        Template = SharedTemplate;
        Focusable = false;
    }

    /// <summary>見出しに表示するセクション名（"CPU" など）。</summary>
    public string SectionTitle
    {
        get => (string)GetValue(SectionTitleProperty);
        set => SetValue(SectionTitleProperty, value);
    }

    /// <summary>折りたたみ時に見出し右側へ表示する要約テキスト（"72% / 4.2GHz / 68°C" など）。</summary>
    public string SectionSummary
    {
        get => (string)GetValue(SectionSummaryProperty);
        set => SetValue(SectionSummaryProperty, value);
    }

    /// <summary>展開時に見出し右側へ表示する副題（モデル名など、"AMD Ryzen 5 5600X ..." のような長い文字列）。
    /// 未設定（既定の空文字）ならヘッダーに何も表示されない。</summary>
    public string SectionSubtitle
    {
        get => (string)GetValue(SectionSubtitleProperty);
        set => SetValue(SectionSubtitleProperty, value);
    }

    /// <summary>展開状態。既定 true。双方向バインド可能。</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>見出し左のアクセントバーの色。</summary>
    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>警告レベル。<see cref="AlertLevel.None"/>（既定）ならアクセントバーは <see cref="AccentBrush"/>
    /// のまま。<see cref="AlertLevel.Caution"/> / <see cref="AlertLevel.Critical"/> ならアクセントバーと
    /// 折りたたみ時の要約テキストの色が警告色に切り替わる。</summary>
    public AlertLevel AlertLevel
    {
        get => (AlertLevel)GetValue(AlertLevelProperty);
        set => SetValue(AlertLevelProperty, value);
    }

    /// <summary>設定（ExpandedSections）に保存するときのキー（"cpu" など）。永続化そのものは呼び出し側が行う。</summary>
    public string SectionKey
    {
        get => (string)GetValue(SectionKeyProperty);
        set => SetValue(SectionKeyProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_headerPart is not null)
        {
            _headerPart.MouseLeftButtonUp -= OnHeaderMouseLeftButtonUp;
        }

        _headerPart = GetTemplateChild(PartHeader) as Border;
        _contentPart = GetTemplateChild(PartContent) as ContentPresenter;
        _summaryPart = GetTemplateChild(PartSummary) as TextBlock;
        _subtitlePart = GetTemplateChild(PartSubtitle) as TextBlock;
        _chevronPart = GetTemplateChild(PartChevron) as Path;

        if (_headerPart is not null)
        {
            _headerPart.MouseLeftButtonUp += OnHeaderMouseLeftButtonUp;
        }

        ApplyExpandedVisualState();
    }

    private void OnHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // SetCurrentValue を使い、ViewModel からの TwoWay バインドを切断せずにトグルする。
        SetCurrentValue(IsExpandedProperty, !IsExpanded);
        e.Handled = true;
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SectionExpander self)
        {
            self.ApplyExpandedVisualState();
        }
    }

    private void ApplyExpandedVisualState()
    {
        bool expanded = IsExpanded;

        if (_contentPart is not null)
        {
            _contentPart.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_summaryPart is not null)
        {
            _summaryPart.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_subtitlePart is not null)
        {
            // SectionSubtitle は展開時のみ表示。SectionSummary とは逆で、コンテンツ本体と同じ扱いにする。
            _subtitlePart.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_chevronPart is not null)
        {
            _chevronPart.Data = expanded ? ChevronDownGeometry : ChevronRightGeometry;
        }
    }

    private static ControlTemplate BuildTemplate()
    {
        FrameworkElementFactory root = new(typeof(Grid));
        root.Name = "PART_Root";

        FrameworkElementFactory rowHeader = new(typeof(RowDefinition));
        rowHeader.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
        FrameworkElementFactory rowContent = new(typeof(RowDefinition));
        rowContent.SetValue(RowDefinition.HeightProperty, GridLength.Auto);

        // RowDefinitions はコレクションプロパティのため、Grid 直下に個別 FrameworkElementFactory として追加する。
        root.AppendChild(rowHeader);
        root.AppendChild(rowContent);

        // ===== ヘッダー行 =====
        FrameworkElementFactory header = new(typeof(Border));
        header.Name = PartHeader;
        header.SetValue(Grid.RowProperty, 0);
        header.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        // 9 セクション全部に効く余白。340px 幅サイドバーの縦の詰まりを緩和するため
        // ヘッダー上下の Padding を 4→2px に切り詰める（CPU/GPU セクション圧縮の一環）。
        header.SetValue(Border.PaddingProperty, new Thickness(0, 2, 0, 2));
        header.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        header.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        FrameworkElementFactory headerGrid = new(typeof(Grid));
        FrameworkElementFactory colAccent = new(typeof(ColumnDefinition));
        colAccent.SetValue(ColumnDefinition.WidthProperty, new GridLength(3));
        // タイトル（"CPU" 等の短い固定文字列）は自然な幅だけを取る。
        // 残りのスペースは colSummary（要約 / 副題）側の Star に譲り、長い副題はそちら側で
        // 縮んで省略記号になる（タイトルが押し出されないようにするため）。
        FrameworkElementFactory colTitle = new(typeof(ColumnDefinition));
        colTitle.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        FrameworkElementFactory colSummary = new(typeof(ColumnDefinition));
        colSummary.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        FrameworkElementFactory colChevron = new(typeof(ColumnDefinition));
        colChevron.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        headerGrid.AppendChild(colAccent);
        headerGrid.AppendChild(colTitle);
        headerGrid.AppendChild(colSummary);
        headerGrid.AppendChild(colChevron);

        FrameworkElementFactory accentBar = new(typeof(Rectangle));
        // 警告レベルに応じてこのバーの色を切り替えるため、トリガーの TargetName で参照できるように名前を付ける
        // （下の ControlTemplate.Triggers 参照）。折りたたんでいてもこのバーは表示され続けるので、
        // 警告表示を高さゼロコストで実現できる。
        accentBar.Name = PartAccentBar;
        accentBar.SetValue(Grid.ColumnProperty, 0);
        accentBar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 8, 1));
        accentBar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        accentBar.SetValue(Shape.FillProperty, new TemplateBindingExtension(AccentBrushProperty));
        accentBar.SetValue(Rectangle.RadiusXProperty, 1.0);
        accentBar.SetValue(Rectangle.RadiusYProperty, 1.0);

        FrameworkElementFactory title = new(typeof(TextBlock));
        title.SetValue(Grid.ColumnProperty, 1);
        title.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(SectionTitleProperty));
        title.SetValue(TextBlock.FontSizeProperty, 12.0);
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFontFamily");
        title.SetResourceReference(TextBlock.ForegroundProperty, "SidebarForegroundBrush");

        FrameworkElementFactory summary = new(typeof(TextBlock));
        summary.Name = PartSummary;
        summary.SetValue(Grid.ColumnProperty, 2);
        summary.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(SectionSummaryProperty));
        summary.SetResourceReference(FrameworkElement.StyleProperty, "SectionSummaryStyle");
        summary.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 6, 0));
        summary.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        // SectionSubtitle（モデル名など）。SectionSummary と同じセルに重ねて配置し、
        // 展開状態に応じてどちらか一方だけを表示する（ApplyExpandedVisualState 参照）。
        // colSummary は Star 幅なので、長い文字列は TextTrimming で省略されタイトル側を圧迫しない。
        FrameworkElementFactory subtitle = new(typeof(TextBlock));
        subtitle.Name = PartSubtitle;
        subtitle.SetValue(Grid.ColumnProperty, 2);
        subtitle.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(SectionSubtitleProperty));
        subtitle.SetResourceReference(FrameworkElement.StyleProperty, "SubTextStyle");
        subtitle.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        subtitle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 6, 0));
        subtitle.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        FrameworkElementFactory chevron = new(typeof(Path));
        chevron.Name = PartChevron;
        chevron.SetValue(Grid.ColumnProperty, 3);
        chevron.SetValue(FrameworkElement.WidthProperty, 10.0);
        chevron.SetValue(FrameworkElement.HeightProperty, 10.0);
        chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chevron.SetValue(Shape.StretchProperty, Stretch.Uniform);
        chevron.SetValue(Path.DataProperty, ChevronDownGeometry);
        chevron.SetResourceReference(Shape.FillProperty, "ChevronBrush");

        headerGrid.AppendChild(accentBar);
        headerGrid.AppendChild(title);
        headerGrid.AppendChild(summary);
        headerGrid.AppendChild(subtitle);
        headerGrid.AppendChild(chevron);
        header.AppendChild(headerGrid);

        // ===== コンテンツ行 =====
        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.Name = PartContent;
        content.SetValue(Grid.RowProperty, 1);
        // ヘッダー Padding 圧縮（4→2px）に合わせてコンテンツ側の上マージンも 3→1px に詰める。
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 0));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));

        root.AppendChild(header);
        root.AppendChild(content);

        ControlTemplate template = new(typeof(SectionExpander))
        {
            VisualTree = root,
        };

        // SourceName で PART_Header 自身の IsMouseOver だけを見る（コンテンツ領域には反応させない）。
        Trigger hoverTrigger = new()
        {
            SourceName = PartHeader,
            Property = UIElement.IsMouseOverProperty,
            Value = true,
        };
        Setter hoverSetter = new(Border.BackgroundProperty, new DynamicResourceExtension("SectionHeaderHoverBrush"))
        {
            TargetName = PartHeader,
        };
        hoverTrigger.Setters.Add(hoverSetter);
        template.Triggers.Add(hoverTrigger);

        // 警告レベルのトリガー。正常(None)時は何もせず AccentBrush / SectionSummaryStyle の色のままにする。
        // Caution/Critical のときだけ、アクセントバーの Fill と（折りたたみ時に見える）要約テキストの
        // Foreground を警告色へ差し替える。新しい要素は増やさず、既存2要素の色を変えるだけなので
        // レイアウトの高さは変わらない。展開時に出る SectionSubtitle（型番などの副題）は対象外。
        template.Triggers.Add(CreateAlertTrigger(AlertLevel.Caution, "AlertCautionBrush"));
        template.Triggers.Add(CreateAlertTrigger(AlertLevel.Critical, "AlertCriticalBrush"));

        template.Seal();
        return template;
    }

    /// <summary>AlertLevel が指定値のときにアクセントバーと要約テキストの色を差し替えるトリガーを作る。</summary>
    private static Trigger CreateAlertTrigger(AlertLevel level, string brushResourceKey)
    {
        Trigger trigger = new()
        {
            Property = AlertLevelProperty,
            Value = level,
        };

        trigger.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension(brushResourceKey))
        {
            TargetName = PartAccentBar,
        });
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(brushResourceKey))
        {
            TargetName = PartSummary,
        });

        return trigger;
    }

    private static Geometry CreateFrozenGeometry(string pathData)
    {
        Geometry geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateFrozenSolid(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
