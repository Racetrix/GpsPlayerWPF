using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // 用于 Toast 延迟
using System.Windows;
using System.Windows.Media;     // 用于颜色
using System.Windows.Media.Animation; // 用于动画
using System.Windows.Threading;

namespace GpsPlayerWPF
{
    // GpsPoint 类保持不变
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
        public string RawLine { get; set; }
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

        // 拖拽Slider的状态
        private bool _isDraggingSlider = false;

        public MainWindow()
        {
            InitializeComponent();
            _renderTimer = new DispatcherTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(33);
            _renderTimer.Tick += RenderTimer_Tick;
        }

        #region 新增功能：置顶与美化弹窗

        // 切换置顶状态
        private void BtnTopmost_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost; // 切换核心属性

            if (this.Topmost)
            {
                BtnTopmost.Content = "📌 已置顶";
                BtnTopmost.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)); // 蓝色高亮
                ShowToast("窗口已置顶，将保持在视频上方");
            }
            else
            {
                BtnTopmost.Content = "📌 置顶窗口";
                BtnTopmost.Background = new SolidColorBrush(Color.FromRgb(68, 68, 68)); // 恢复灰色
                ShowToast("已取消置顶");
            }
        }

        // 显示 Toast 动画 (替代 MessageBox)
        private async void ShowToast(string message)
        {
            ToastText.Text = message;

            // 定义淡入动画
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            ToastOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            // 等待 2.5 秒
            await Task.Delay(2500);

            // 定义淡出动画
            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
            ToastOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        #endregion

        // 以下逻辑与之前保持一致，但把 MessageBox 替换为 ShowToast

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

                _headers = lines[0].Split(',').Select(h => h.Trim()).ToList();
                var dateRegex = new Regex(@"^\d{4}-\d{2}-\d{2}");

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cleanLine = line.Replace("\0", "").Trim();
                    var cols = cleanLine.Split(',');
                    if (cols.Length < _headers.Count) continue;

                    if (DateTime.TryParse(cols[0], out DateTime dt))
                    {
                        var p = new GpsPoint
                        {
                            Timestamp = dt,
                            TimeStr = cols[0],
                            RawLine = cleanLine
                        };
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
                    // 【修改】使用美化的 Toast 提示
                    ShowToast($"成功加载 {_data.Count} 条数据");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取错误: {ex.Message}"); // 错误还是建议用强弹窗
            }
        }

        private string GetVal(string[] cols, string colName)
        {
            int idx = _headers.IndexOf(colName);
            if (idx != -1 && idx < cols.Length) return cols[idx];
            return "0";
        }

        private double ParseDouble(string val)
        {
            if (double.TryParse(val, out double result)) return result;
            return 0.0;
        }

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
            if (_currentIndex >= _data.Count - 1) _currentIndex = 0;
            _startDataTime = _data[_currentIndex].Timestamp;
            _renderTimer.Start();
        }

        private void StopPlay()
        {
            _isPlaying = false;
            BtnPlay.Content = "▶ 播放";
            BtnPlay.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
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
            for (int i = _currentIndex; i < _data.Count; i++)
            {
                if (_data[i].Timestamp >= targetDataTime)
                {
                    newIndex = i;
                    break;
                }
                if (i == _data.Count - 1) endReached = true;
            }

            if (endReached && targetDataTime > _data.Last().Timestamp)
            {
                StopPlay();
                newIndex = _data.Count - 1;
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
            LblPos.Text = $"{p.Lat:F6} / {p.Lon:F6}";
            LblHeading.Text = $"{p.Heading:F0}°";

            if (!_isDraggingSlider) TimeSlider.Value = index;
            LblProgress.Text = $"{index + 1} / {_data.Count}";
        }

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

        private void BtnCut_Click(object sender, RoutedEventArgs e)
        {
            if (_data.Count == 0) return;
            var startPoint = _data[_currentIndex];
            // 切割这种重要操作，依然保留确认弹窗
            var result = MessageBox.Show(
                $"确定要切割文件吗？\n\n起始时间：{startPoint.TimeStr}",
                "切割确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                SaveCutFile(startPoint.Timestamp);
            }
        }

        private void SaveCutFile(DateTime startTime)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV Files|*.csv",
                FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + "_cut.csv"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    using (StreamWriter sw = new StreamWriter(sfd.FileName, false, Encoding.UTF8))
                    {
                        sw.WriteLine(string.Join(",", _headers));
                        var cutData = _data.Where(d => d.Timestamp >= startTime).ToList();
                        foreach (var item in cutData) sw.WriteLine(item.RawLine);
                    }
                    // 【修改】保存成功也使用 Toast
                    ShowToast("切割文件已保存！");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}");
                }
            }
        }
    }
}