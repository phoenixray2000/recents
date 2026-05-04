

## Context

The favorites sidebar currently has two problems:
1. Any mouse movement >4px while holding the button over a favorites item triggers `DoDragDrop` with `InternalReorder`, which can fire accidentally on a normal click and block the UI thread (causing noticeable double-click delay).
2. The unpin button (32px) takes up space that is wasted in the common read-only case; combined with the fixed 280px column width it dominates the window footprint.

The solution: introduce an `IsFavoritesEditMode` toggle. In normal mode the unpin button is hidden and `MouseMove` only does external file drag. In edit mode the unpin button and a drag handle are visible, and `InternalReorder` drag-drop is activated from the handle.

Width drops from 280→140px (total window 880→740px). File names get ~105px of space (no worse than today's ~200px – 32px button overlap at smaller scale, and tooltip covers the rest).

---

## Change Flow

```
MainViewModel
  + IsFavoritesEditMode (ObservableProperty, bool)
  + ToggleFavoritesEditModeCommand (RelayCommand)
        │
        ▼
MainWindow.xaml
  ┌─ Favorites header row:  "FAVORITES" label + Edit/Done button
  ├─ FavoritesItemTemplate:
  │     Col 0: drag-handle Button  (visible only in EditMode)
  │     Col 1: 24px icon
  │     Col 2: DisplayName text
  │     Col 3: unpin Button        (visible only in EditMode)
  └─ Border Width: 280 → 140
        │
        ▼
MainWindow.xaml.cs
  ├─ UpdateWindowWidth(): 280 → 140
  └─ FavoritesList_MouseMove:
        normal mode  → external file drag only (no InternalReorder data)
        edit mode    → drag-handle button triggers InternalReorder drag
```

---

## Step-by-Step Changes

### 1. `MainViewModel.cs`

Add below `_isFavoritesDrawerOpen`:

```csharp
[ObservableProperty]
private bool _isFavoritesEditMode = false;

[RelayCommand]
private void ToggleFavoritesEditMode() => IsFavoritesEditMode = !IsFavoritesEditMode;
```

### 2. `MainWindow.xaml` — `FavoritesItemTemplate`

Replace the current template (lines 153–173) with a 4-column grid:

```xml
<DataTemplate x:Key="FavoritesItemTemplate" DataType="{x:Type vm:RecentItemViewModel}">
    <Grid Margin="2,0">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" />   <!-- drag handle (edit mode only) -->
            <ColumnDefinition Width="Auto" />   <!-- 24px icon -->
            <ColumnDefinition Width="*" />      <!-- display name -->
            <ColumnDefinition Width="Auto" />   <!-- unpin button (edit mode only) -->
        </Grid.ColumnDefinitions>

        <!-- drag handle: visible in edit mode -->
        <Button Grid.Column="0" Content="&#xE784;" x:Name="DragHandle"
                FontFamily="Segoe Fluent Icons" Width="24" Height="32"
                Style="{StaticResource ActionButtonStyle}"
                Foreground="{DynamicResource TextTertiary}"
                ToolTip="Drag to reorder"
                PreviewMouseLeftButtonDown="FavoritesDragHandle_PreviewMouseLeftButtonDown">
            <Button.Style>
                <Style TargetType="Button" BasedOn="{StaticResource ActionButtonStyle}">
                    <Setter Property="Visibility" Value="Collapsed"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding DataContext.IsFavoritesEditMode,
                                     RelativeSource={RelativeSource AncestorType=Window}}"
                                     Value="True">
                            <Setter Property="Visibility" Value="Visible"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Button.Style>
        </Button>

        <Image Grid.Column="1" Source="{Binding SmallIcon}" Width="24" Height="24"
               VerticalAlignment="Center" Margin="0,0,8,0"/>

        <TextBlock Grid.Column="2" Text="{Binding DisplayName}" FontWeight="SemiBold"
                   FontSize="12" Foreground="{DynamicResource TextPrimary}"
                   TextTrimming="CharacterEllipsis" VerticalAlignment="Center"
                   ToolTip="{Binding DisplayPath}"/>

        <!-- unpin button: visible in edit mode -->
        <Button Grid.Column="3" Content="&#xE735;" Command="{Binding TogglePinCommand}"
                FontFamily="Segoe Fluent Icons" Width="28" Height="32"
                ToolTip="{loc:T Key=Main_Favorites_Unpin}"
                Style="{StaticResource ActionButtonStyle}"
                Foreground="{DynamicResource AccentBlue}">
            <Button.Style>
                <Style TargetType="Button" BasedOn="{StaticResource ActionButtonStyle}">
                    <Setter Property="Visibility" Value="Collapsed"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding DataContext.IsFavoritesEditMode,
                                     RelativeSource={RelativeSource AncestorType=Window}}"
                                     Value="True">
                            <Setter Property="Visibility" Value="Visible"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Button.Style>
        </Button>
    </Grid>
</DataTemplate>
```

### 3. `MainWindow.xaml` — Favorites header row

Replace the current header (lines 442–446):

```xml
<Grid Grid.Row="0" Margin="8,0">
    <TextBlock Text="{loc:T Key=Main_Favorites_Header}"
               FontWeight="Bold" FontSize="11"
               Foreground="{DynamicResource TextSecondary}"
               VerticalAlignment="Center" Margin="8,0,0,0"/>
    <Button HorizontalAlignment="Right" VerticalAlignment="Center"
            Command="{Binding ToggleFavoritesEditModeCommand}"
            Style="{StaticResource ActionButtonStyle}"
            Width="Auto" Padding="8,4">
        <Button.Content>
            <TextBlock FontSize="11" Foreground="{DynamicResource TextSecondary}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Setter Property="Text" Value="{loc:T Key=Main_Favorites_Edit}"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsFavoritesEditMode}" Value="True">
                                <Setter Property="Text" Value="{loc:T Key=Main_Favorites_Done}"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </Button.Content>
    </Button>
</Grid>
```

### 4. `MainWindow.xaml` — Border width

Line 433: `Width="280"` → `Width="140"`

### 5. `MainWindow.xaml.cs` — `UpdateWindowWidth()`

Line 182: `Width = _baseWidth + 280;` → `Width = _baseWidth + 140;`

### 6. `MainWindow.xaml.cs` — drag logic split

**Add** a new handler `FavoritesDragHandle_PreviewMouseLeftButtonDown` that records start position and item, then arm the drag from the handle:

```csharp
private RecentItemViewModel? _favoritesDragItem;

private void FavoritesDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (sender is FrameworkElement fe && fe.DataContext is RecentItemViewModel vm)
    {
        _favoritesDragItem = vm;
        _dragStartPoint = e.GetPosition(null);
        e.Handled = false; // let MouseMove fire
    }
}
```

**Replace** `FavoritesList_MouseMove` (lines 434–456):

```csharp
private void FavoritesList_MouseMove(object sender, WpfMouseEventArgs e)
{
    if (e.LeftButton != MouseButtonState.Pressed || !_dragStartPoint.HasValue || FavoritesList.SelectedItem == null)
        return;

    var currentPos = e.GetPosition(null);
    if (Math.Abs(currentPos.X - _dragStartPoint.Value.X) <= SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(currentPos.Y - _dragStartPoint.Value.Y) <= SystemParameters.MinimumVerticalDragDistance)
        return;

    var source = e.OriginalSource as DependencyObject;

    if (_viewModel.IsFavoritesEditMode && _favoritesDragItem != null)
    {
        // Edit mode: internal reorder only, no external drop data
        var vm = _favoritesDragItem;
        _favoritesDragItem = null;
        _dragStartPoint = null;
        var dataObj = new System.Windows.DataObject();
        dataObj.SetData("InternalReorder", vm);
        System.Windows.DragDrop.DoDragDrop(FavoritesList, dataObj, System.Windows.DragDropEffects.Move);
    }
    else if (!_viewModel.IsFavoritesEditMode)
    {
        // Normal mode: external file drag only, no reorder
        if (FindParent<System.Windows.Controls.Button>(source) != null) return;
        var selected = FavoritesList.SelectedItem as RecentItemViewModel;
        if (selected == null || selected.IsMissing) return;
        _dragStartPoint = null;
        var dataObj = DragDropService.CreateDataObject(new[] { selected.DisplayPath });
        System.Windows.DragDrop.DoDragDrop(FavoritesList, dataObj,
            System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
    }
}
```

### 7. Localization — add Edit/Done keys

`Strings.resx`:
```xml
<data name="Main_Favorites_Edit" xml:space="preserve"><value>Edit</value></data>
<data name="Main_Favorites_Done" xml:space="preserve"><value>Done</value></data>
```

`Strings.zh-CN.resx`:
```xml
<data name="Main_Favorites_Edit" xml:space="preserve"><value>编辑</value></data>
<data name="Main_Favorites_Done" xml:space="preserve"><value>完成</value></data>
```

---

## Critical Files

| File | Lines affected |
|------|---------------|
| `src/Recents.App/ViewModels/MainViewModel.cs` | After line 54 (new property + command) |
| `src/Recents.App/MainWindow.xaml` | Lines 153–173 (template), 433 (width), 442–446 (header) |
| `src/Recents.App/MainWindow.xaml.cs` | Lines 176–188 (UpdateWindowWidth), 434–456 (FavoritesList_MouseMove), + new field + new handler |
| `src/Recents.App/Localization/Strings.resx` | After line 74 |
| `src/Recents.App/Localization/Strings.zh-CN.resx` | After line 74 |

---

## Verification

1. Build: `dotnet build src/Recents.App/Recents.App.csproj`
2. **Normal mode**: Open app with favorites → sidebar shows only icon + name (no unpin, no handle). Window total width ~740px. Double-click a favorites item → opens. Drag a favorites item to desktop/Explorer → file drops correctly (external drag works). No accidental reorder drag.
3. **Edit mode**: Click "编辑/Edit" in header → drag handles and unpin buttons appear. Drag a handle → item reorders in the list (internal only; dragging to desktop does nothing). Click unpin → removes from favorites. Click "完成/Done" → returns to normal mode.
4. **Double-click**: In normal mode, double-click a favorites item → file opens without reorder drag interfering.
5. **External drop onto favorites**: Drag a file from Explorer onto the sidebar → added as favorite (existing `FavoritesList_Drop` unchanged).