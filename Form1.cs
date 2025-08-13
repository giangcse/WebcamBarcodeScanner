using System;
using System.Data;
using System.Data.SQLite;
using System.Drawing;
using System.IO; // Thêm thư viện này
using System.Linq;
using System.Windows.Forms;
using AForge.Video;
using AForge.Video.DirectShow;
using ClosedXML.Excel;
using ZXing;

namespace WebcamBarcodeScanner
{
    public partial class Form1 : Form
    {
        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private ResultPoint[] lastResultPoints;

        // === PHẦN THAY ĐỔI QUAN TRỌNG ===

        /// <summary>
        /// Lấy đường dẫn đầy đủ đến file CSDL trong thư mục AppData của người dùng.
        /// </summary>
        /// <returns>Đường dẫn tới file database.</returns>
        private static string GetDatabasePath()
        {
            // Lấy thư mục AppData\Local - nơi an toàn để lưu dữ liệu ứng dụng
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Tạo một thư mục con cho ứng dụng của bạn để tránh lộn xộn
            string appFolder = Path.Combine(appDataPath, "BarcodeScannerApp");
            Directory.CreateDirectory(appFolder); // Lệnh này sẽ tạo thư mục nếu nó chưa tồn tại

            // Kết hợp đường dẫn với tên file CSDL
            return Path.Combine(appFolder, "ScanHistory.sqlite");
        }

        // Sử dụng phương thức mới để lấy đường dẫn
        private static readonly string dbFile = GetDatabasePath();
        private static readonly string connectionString = $"Data Source={dbFile};Version=3;";

        // === KẾT THÚC PHẦN THAY ĐỔI ===

        public Form1()
        {
            InitializeComponent();
        }

        // Các hàm còn lại giữ nguyên không thay đổi...
        // ... (Bạn chỉ cần copy toàn bộ code này và dán đè lên file Form1.cs cũ là được) ...

        private void Form1_Load(object sender, EventArgs e)
        {
            InitializeDatabase();
            LoadDataToGrid();
            InitializeCamera();
        }

        #region Database Methods

        private void InitializeDatabase()
        {
            // Bây giờ ứng dụng sẽ tạo file CSDL ở một nơi nó có quyền ghi
            if (!File.Exists(dbFile))
            {
                SQLiteConnection.CreateFile(dbFile);
            }

            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                string createTableSql = "CREATE TABLE IF NOT EXISTS ScanHistory (ID INTEGER PRIMARY KEY AUTOINCREMENT, Result TEXT, ScanDate TEXT, ScanTime TEXT)";
                using (var cmd = new SQLiteCommand(createTableSql, conn))
                {
                    cmd.ExecuteNonQuery();
                }

                var cmdCheck = new SQLiteCommand("PRAGMA table_info(ScanHistory)", conn);
                var reader = cmdCheck.ExecuteReader();
                bool columnExists = false;
                while (reader.Read())
                {
                    if (reader["name"].ToString().Equals("ScanTime", StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }
                reader.Close();

                if (!columnExists)
                {
                    var cmdAlter = new SQLiteCommand("ALTER TABLE ScanHistory ADD COLUMN ScanTime TEXT", conn);
                    cmdAlter.ExecuteNonQuery();
                }
            }
        }

        private void InsertScanResult(string result)
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                string sql = "INSERT INTO ScanHistory (Result, ScanDate, ScanTime) VALUES (@result, @scanDate, @scanTime)";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@result", result);
                    cmd.Parameters.AddWithValue("@scanDate", DateTime.Now.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("@scanTime", DateTime.Now.ToString("HH:mm:ss"));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void LoadDataToGrid()
        {
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                string sql = "SELECT ID, Result, ScanDate, ScanTime FROM ScanHistory ORDER BY ID DESC";
                using (var adapter = new SQLiteDataAdapter(sql, conn))
                {
                    DataTable dt = new DataTable();
                    adapter.Fill(dt);
                    dgvHistory.DataSource = dt;

                    if (dgvHistory.Columns.Count > 0)
                    {
                        dgvHistory.Columns["ID"].HeaderText = "STT";
                        dgvHistory.Columns["Result"].HeaderText = "Kết quả";
                        dgvHistory.Columns["ScanDate"].HeaderText = "Ngày quét";
                        dgvHistory.Columns["ScanTime"].HeaderText = "Giờ quét";

                        dgvHistory.Columns["ID"].FillWeight = 10;
                        dgvHistory.Columns["Result"].FillWeight = 50;
                        dgvHistory.Columns["ScanDate"].FillWeight = 20;
                        dgvHistory.Columns["ScanTime"].FillWeight = 20;
                    }
                }
            }
        }
        #endregion

        #region Camera Control
        private void InitializeCamera()
        {
            videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            if (videoDevices.Count == 0)
            {
                MessageBox.Show("Không tìm thấy webcam nào!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnStart.Enabled = false;
                return;
            }
            foreach (FilterInfo device in videoDevices)
            {
                cboDevices.Items.Add(device.Name);
            }
            cboDevices.SelectedIndex = 0;
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            if (videoSource != null && videoSource.IsRunning) StopCamera();
            else StartCamera();
        }

        private void StartCamera()
        {
            try
            {
                videoSource = new VideoCaptureDevice(videoDevices[cboDevices.SelectedIndex].MonikerString);
                videoSource.NewFrame += new NewFrameEventHandler(video_NewFrame);
                videoSource.Start();
                timerScan.Start();
                btnStart.Text = "Dừng";
                cboDevices.Enabled = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi khởi động camera: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopCamera()
        {
            if (videoSource != null && videoSource.IsRunning)
            {
                timerScan.Stop();
                videoSource.NewFrame -= new NewFrameEventHandler(video_NewFrame);
                videoSource.SignalToStop();
                videoSource.WaitForStop();
                videoSource = null;
                lastResultPoints = null;
                picVideo.Image = null;
                btnStart.Text = "Bắt đầu";
                cboDevices.Enabled = true;
            }
        }

        private void video_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            Bitmap frame = (Bitmap)eventArgs.Frame.Clone();
            if (lastResultPoints != null && lastResultPoints.Length > 0)
            {
                Point[] points = lastResultPoints.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
                using (Graphics g = Graphics.FromImage(frame))
                {
                    using (Pen pen = new Pen(Color.LimeGreen, 3))
                    {
                        g.DrawPolygon(pen, points);
                    }
                }
            }
            picVideo.Image = frame;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopCamera();
        }
        #endregion

        #region Scanning and Export
        private void timerScan_Tick(object sender, EventArgs e)
        {
            if (picVideo.Image == null) return;

            BarcodeReader reader = new BarcodeReader { Options = { TryHarder = true } };
            Result result = reader.Decode((Bitmap)picVideo.Image);

            if (result != null)
            {
                lastResultPoints = result.ResultPoints;
                txtResult.Invoke(new MethodInvoker(delegate ()
                {
                    string decodedText = result.ToString();
                    if (txtResult.Text != decodedText)
                    {
                        txtResult.Text = decodedText;
                        Clipboard.SetText(decodedText);
                        System.Media.SystemSounds.Beep.Play();
                        InsertScanResult(decodedText);
                        LoadDataToGrid();
                    }
                }));
            }
            else
            {
                lastResultPoints = null;
            }
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            try
            {
                string fromDate = dtpFromDate.Value.ToString("yyyy-MM-dd");
                string toDate = dtpToDate.Value.ToString("yyyy-MM-dd");

                DataTable dt = new DataTable();
                using (var conn = new SQLiteConnection(connectionString))
                {
                    conn.Open();
                    string sql = "SELECT Result, ScanDate, ScanTime FROM ScanHistory WHERE ScanDate BETWEEN @fromDate AND @toDate ORDER BY ID ASC";
                    using (var adapter = new SQLiteDataAdapter(sql, conn))
                    {
                        adapter.SelectCommand.Parameters.AddWithValue("@fromDate", fromDate);
                        adapter.SelectCommand.Parameters.AddWithValue("@toDate", toDate);
                        adapter.Fill(dt);
                    }
                }

                if (dt.Rows.Count == 0)
                {
                    MessageBox.Show("Không có dữ liệu để xuất trong khoảng thời gian đã chọn.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                dt.Columns["Result"].ColumnName = "Kết quả";
                dt.Columns["ScanDate"].ColumnName = "Ngày quét";
                dt.Columns["ScanTime"].ColumnName = "Giờ quét";

                using (SaveFileDialog sfd = new SaveFileDialog() { Filter = "Excel Workbook|*.xlsx", ValidateNames = true })
                {
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        using (var workbook = new XLWorkbook())
                        {
                            workbook.Worksheets.Add(dt, "LichSuQuet");
                            var worksheet = workbook.Worksheet(1);
                            worksheet.Columns().AdjustToContents();
                            workbook.SaveAs(sfd.FileName);
                        }
                        MessageBox.Show("Xuất file Excel thành công!", "Thành công", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Đã xảy ra lỗi khi xuất file: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        #endregion
    }
}