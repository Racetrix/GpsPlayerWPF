using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GpsPlayerWPF
{
    // 数据模型
    public class GpsPoint
    {
        public DateTime Timestamp { get; set; }
        public string TimeStr { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Alt { get; set; }
        public double Speed { get; set; }
        public int Sats { get; set; }
        public double Heading { get; set; }
        public string RawLine { get; set; } // 保存原始行，用于无损保存
    }

    public partial class MainWindow : Window
    {
        private List<GpsPoint> _data = new List<GpsPoint>();
        private string _currentFilePath = "";
        private List<string> _headers = new List<string>();

        private bool _isPlaying = false;
        private int _currentIndex = 0;
        private DispatcherTimer _renderTimer;
        private Stopwatch _stopwatch = new Stopwatch();
        private DateTime _startDataTime;

        private int? _markInIndex = null;
        private int? _markOutIndex = null;

        private bool _isDraggingSlider = false;

        // 【核心修改】强力正则：专门处理 '2026-01-1400:05:40' 这种粘连格式
        private static readonly Regex TimeFixRegex = new Regex(@"^(\d{4}-\d{2}-\d{2})\s*(\d{2}:\d{2}:\d{2}\.\d+)");

        public MainWindow()
        {
            InitializeComponent();
            _renderTimer = new DispatcherTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(33); // 约 30 FPS
            _renderTimer.Tick += RenderTimer_Tick;
        }

        #region 新增功能：置顶与美化弹窗 (保持不变)

        private void BtnTopmost_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
            if (this.Topmost)
            {
                BtnTopmost.Content = "📌 已置顶";
                BtnTopmost.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                ShowToast("窗口已置顶，将保持在视频上方");
            }
            else
            {
                BtnTopmost.Content = "📌 置顶窗口";
                BtnTopmost.Background = new SolidColorBrush(Color.FromRgb(68, 68, 68));
                ShowToast("已取消置顶");
            }
        }

        private void BtnMarkIn_Click(object sender, RoutedEventArgs e)
        {
            if (_data.Count == 0) return;

            _markInIndex = _currentIndex; // 记录当前播放位置的索引
            UpdateSelectionUi();
        }

        // 2. 设置终点 (Mark Out)
        private void BtnMarkOut_Click(object sender, RoutedEventArgs e)
        {
            if (_data.Count == 0) return;

            _markOutIndex = _currentIndex; // 记录当前播放位置的索引
            UpdateSelectionUi();
        }

        // 3. 清除打点
        private void BtnClearMarks_Click(object sender, RoutedEventArgs e)
        {
            _markInIndex = null;
            _markOutIndex = null;
            UpdateSelectionUi();
        }

        // 4. 更新界面显示的文字
        private void UpdateSelectionUi()
        {
            if (_markInIndex == null && _markOutIndex == null)
            {
                LblSelectionRange.Text = "未选择范围";
                return;
            }

            string startStr = _markInIndex.HasValue ? _data[_markInIndex.Value].TimeStr.Split(' ')[1] : "--:--";
            string endStr = _markOutIndex.HasValue ? _data[_markOutIndex.Value].TimeStr.Split(' ')[1] : "--:--";

            // 计算时长 (如果两个都设置了)
            string duration = "";
            if (_markInIndex.HasValue && _markOutIndex.HasValue)
            {
                var diff = Math.Abs(_markOutIndex.Value - _markInIndex.Value);
                // 假设 10Hz (100ms) 简单估算，或者直接用 Timestamp 减
                var timeDiff = (_data[_markOutIndex.Value].Timestamp - _data[_markInIndex.Value].Timestamp).TotalSeconds;
                duration = $" ({Math.Abs(timeDiff):F1}s)";
            }

            LblSelectionRange.Text = $"{startStr} ~ {endStr}{duration}";
        }

        // 5. 保存选中段 (核心功能)
        private void BtnSaveRange_Click(object sender, RoutedEventArgs e)
        {
            if (_markInIndex == null || _markOutIndex == null)
            {
                ShowToast("请先设置起点(A)和终点(B)！"); // 假设你保留了 ShowToast
                                               // MessageBox.Show("请先设置起点(A)和终点(B)！"); 
                return;
            }

            // 自动处理：不管用户先点的哪一个，我们取 Min 作为开始，Max 作为结束
            int start = Math.Min(_markInIndex.Value, _markOutIndex.Value);
            int end = Math.Max(_markInIndex.Value, _markOutIndex.Value);
            int count = end - start + 1;

            var startTimeStr = _data[start].TimeStr;
            var endTimeStr = _data[end].TimeStr;

            var result = MessageBox.Show(
                $"确定保存这段数据吗？\n\n起点：{startTimeStr}\n终点：{endTimeStr}\n总计：{count} 行数据",
                "保存范围", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // 使用 LINQ 提取范围：Skip 跳过前面，Take 拿取中间
                var rangeData = _data.Skip(start).Take(count).ToList();
                SaveDataList(rangeData, "_clip");
            }
        }

        // 通用保存 List<GpsPoint> 的方法
        private void SaveDataList(List<GpsPoint> pointsToSave, string suffix)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV Files|*.csv",
                FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + suffix + ".csv"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    using (StreamWriter sw = new StreamWriter(sfd.FileName, false, Encoding.UTF8))
                    {
                        sw.WriteLine(string.Join(",", _headers)); // 写表头
                        foreach (var item in pointsToSave)
                        {
                            sw.WriteLine(item.RawLine); // 写原始行
                        }
                    }
                    ShowToast($"成功保存片段！");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}");
                }
            }
        }

        private async void ShowToast(string message)
        {
            ToastText.Text = message;
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            ToastOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            await Task.Delay(2500);

            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
            ToastOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        #endregion

        // --- 核心修改：Load Logic ---

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "CSV Files|*.csv" };
            if (ofd.ShowDialog() == true)
            {
                LoadCsv(ofd.FileName);
            }
        }

        private void LoadCsv(string path)
        {
            StopPlay();
            _data.Clear();
            _headers.Clear();
            _currentFilePath = path;
            TxtFilePath.Text = Path.GetFileName(path);

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2) return;

                // 1. 读取表头，用于动态查找列索引
                _headers = lines[0].Split(',').Select(h => h.Trim()).ToList();

                // 2. 遍历数据
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cleanLine = line.Replace("\0", "").Trim();
                    var cols = cleanLine.Split(',');
                    if (cols.Length < _headers.Count) continue;

                    // --- 【重点修改】时间解析逻辑 ---
                    string rawTime = cols[0].Trim(); // 假设时间永远在第一列
                    DateTime dt = DateTime.MinValue;
                    bool parseSuccess = false;

                    // A. 尝试标准解析
                    if (DateTime.TryParse(rawTime, out dt))
                    {
                        parseSuccess = true;
                    }
                    // B. 尝试正则修复 (针对 1400:05 这种粘连情况)
                    else
                    {
                        var match = TimeFixRegex.Match(rawTime);
                        if (match.Success)
                        {
                            // 强制加空格：2026-01-14 + " " + 00:05:40.004
                            string fixedTime = $"{match.Groups[1].Value} {match.Groups[2].Value}";
                            if (DateTime.TryParse(fixedTime, out dt))
                            {
                                parseSuccess = true;
                            }
                        }
                    }

                    // 只有时间解析成功才添加数据
                    if (parseSuccess)
                    {
                        var p = new GpsPoint
                        {
                            Timestamp = dt,
                            TimeStr = rawTime, // 存原始的，防止写回 CSV 时变样
                            RawLine = cleanLine // 存整行，用于完美复制
                        };

                        // 使用动态列名读取 (兼容列序变化)
                        p.Lat = ParseDouble(GetVal(cols, "Lat"));
                        p.Lon = ParseDouble(GetVal(cols, "Lon"));
                        p.Alt = ParseDouble(GetVal(cols, "Alt"));
                        p.Speed = ParseDouble(GetVal(cols, "Speed_kmh"));
                        p.Heading = ParseDouble(GetVal(cols, "Heading"));

                        int.TryParse(GetVal(cols, "Sats"), out int sats);
                        p.Sats = sats;

                        _data.Add(p);
                    }
                }

                if (_data.Count > 0)
                {
                    TimeSlider.Maximum = _data.Count - 1;
                    UpdateUi(0);
                    ShowToast($"成功加载 {_data.Count} 条数据");
                }
                else
                {
                    MessageBox.Show("没有解析到有效数据，请检查 CSV 格式。");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取错误: {ex.Message}");
            }
        }

        // 辅助方法：根据列名找值
        private string GetVal(string[] cols, string colName)
        {
            int idx = _headers.IndexOf(colName);
            // 只有当该列存在，且当前行有足够多的列时才返回
            if (idx != -1 && idx < cols.Length) return cols[idx];
            return "0";
        }

        private double ParseDouble(string val)
        {
            if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result))
                return result;
            return 0.0;
        }

        // --- 播放控制逻辑 (基本保持不变) ---

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_data.Count == 0) return;
            if (_isPlaying) StopPlay();
            else StartPlay();
        }

        private void StartPlay()
        {
            _isPlaying = true;
            BtnPlay.Content = "⏸ 暂停";
            BtnPlay.Background = new SolidColorBrush(Colors.IndianRed);
            _stopwatch.Restart();

            // 如果已经在末尾，从头开始
            if (_currentIndex >= _data.Count - 1) _currentIndex = 0;

            _startDataTime = _data[_currentIndex].Timestamp;
            _renderTimer.Start();
        }

        private void StopPlay()
        {
            _isPlaying = false;
            BtnPlay.Content = "▶ 播放";
            BtnPlay.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            _renderTimer.Stop();
            _stopwatch.Stop();
        }

        private void RenderTimer_Tick(object sender, EventArgs e)
        {
            if (!_isPlaying || _data.Count == 0) return;

            TimeSpan elapsedRealTime = _stopwatch.Elapsed;
            DateTime targetDataTime = _startDataTime + elapsedRealTime;

            int newIndex = _currentIndex;
            bool endReached = false;

            // 简单的向前搜索
            for (int i = _currentIndex; i < _data.Count; i++)
            {
                if (_data[i].Timestamp >= targetDataTime)
                {
                    newIndex = i;
                    break;
                }
                if (i == _data.Count - 1) endReached = true;
            }

            // 播放结束判定
            if (endReached && targetDataTime > _data.Last().Timestamp)
            {
                StopPlay();
                newIndex = _data.Count - 1;
                ShowToast("播放结束");
            }

            if (newIndex != _currentIndex)
            {
                _currentIndex = newIndex;
                UpdateUi(_currentIndex);
            }
        }

        private void UpdateUi(int index)
        {
            if (index < 0 || index >= _data.Count) return;
            var p = _data[index];

            LblTime.Text = p.Timestamp.ToString("HH:mm:ss.ff");
            LblSpeed.Text = p.Speed.ToString("F1");
            LblSats.Text = p.Sats.ToString();
            LblAlt.Text = p.Alt.ToString("F1");
            LblPos.Text = $"{p.Lat:F6} / {p.Lon:F6}"; // 优化显示格式
            LblHeading.Text = $"{p.Heading:F0}°";

            if (!_isDraggingSlider) TimeSlider.Value = index;
            LblProgress.Text = $"{index + 1} / {_data.Count}";
        }

        // --- 交互逻辑 ---

        private void TimeSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
            if (_isPlaying) StopPlay();
        }

        private void TimeSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            _currentIndex = (int)TimeSlider.Value;
            UpdateUi(_currentIndex);
        }

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSlider) UpdateUi((int)e.NewValue);
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            StopPlay();
            _currentIndex = Math.Max(0, _currentIndex - 1);
            UpdateUi(_currentIndex);
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            StopPlay();
            _currentIndex = Math.Min(_data.Count - 1, _currentIndex + 1);
            UpdateUi(_currentIndex);
        }

        // --- 切割功能 (Cut / Trim) ---
        // 这里的逻辑是：保留当前点之后的所有数据 (向后裁切 / Trim Start)

        // --- 新增/修改的裁切逻辑 ---

        // 1. 保留前段 (Keep Front / Trim End)
        private void BtnTrimKeepFront_Click(object sender, RoutedEventArgs e)
        {
            PerformTrim(isKeepFront: true);
        }

        // 2. 保留后段 (Keep Back / Trim Start)
        private void BtnTrimKeepBack_Click(object sender, RoutedEventArgs e)
        {
            PerformTrim(isKeepFront: false);
        }

        // 通用裁切执行方法
        private void PerformTrim(bool isKeepFront)
        {
            if (_data.Count == 0) return;

            var currentPoint = _data[_currentIndex];
            string actionName = isKeepFront ? "保留前段" : "保留后段";
            string desc = isKeepFront
                ? $"将保留 {currentPoint.TimeStr} **之前** 的数据\n(之后的数据将被丢弃)"
                : $"将保留 {currentPoint.TimeStr} **之后** 的数据\n(之前的数据将被丢弃)";

            var result = MessageBox.Show(
                $"确定要执行【{actionName}】吗？\n\n{desc}",
                "裁切确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // 传入筛选条件
                if (isKeepFront)
                {
                    //保留 <= 当前时间的数据，文件后缀 _head
                    SaveTrimmedData(d => d.Timestamp <= currentPoint.Timestamp, "_head");
                }
                else
                {
                    //保留 >= 当前时间的数据，文件后缀 _tail
                    SaveTrimmedData(d => d.Timestamp >= currentPoint.Timestamp, "_tail");
                }
            }
        }

        // 通用保存方法 (接收一个筛选函数 predicate)
        private void SaveTrimmedData(Func<GpsPoint, bool> predicate, string suffix)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV Files|*.csv",
                // 自动生成文件名，例如: data_head.csv 或 data_tail.csv
                FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + suffix + ".csv"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    using (StreamWriter sw = new StreamWriter(sfd.FileName, false, Encoding.UTF8))
                    {
                        // 1. 写入原来的表头
                        sw.WriteLine(string.Join(",", _headers));

                        // 2. 使用传入的条件筛选数据
                        var trimmedData = _data.Where(predicate).ToList();

                        // 3. 写入原始行 (RawLine)
                        foreach (var item in trimmedData)
                        {
                            sw.WriteLine(item.RawLine);
                        }

                        // 4. 显示成功提示
                        ShowToast($"已保存 {trimmedData.Count} 条数据！");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}");
                }
            }
        }
    }
}