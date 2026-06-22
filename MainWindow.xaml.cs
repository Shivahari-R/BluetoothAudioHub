using System;
using System.Collections.Generic;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BluetoothAudioHub
{
    public partial class MainWindow : Window
    {
        private bool isRunning = false;
        private WasapiLoopbackCapture systemCapture;
        private WasapiOut outputDeviceA;
        private WasapiOut outputDeviceB;
        private BufferedWaveProvider bufferA;
        private BufferedWaveProvider bufferB;
        private VolumeSampleProvider volumeNodeA;
        private VolumeSampleProvider volumeNodeB;

        // NEW: Windows Master Volume trackers
        private MMDevice defaultWindowsDevice;
        private float systemMasterVolume = 1.0f;

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();
        }

        private void LoadDevices()
        {
            var devicesA = new List<AudioDevice>();
            var devicesB = new List<AudioDevice>();

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var audioEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);

                foreach (var endpoint in audioEndpoints)
                {
                    try
                    {
                        string statusIcon = endpoint.State == DeviceState.Active ? "🟢" : "🔴";
                        string displayName = $"{statusIcon} {endpoint.FriendlyName}";

                        devicesA.Add(new AudioDevice { Name = displayName, Device = endpoint });
                        devicesB.Add(new AudioDevice { Name = displayName, Device = endpoint });
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                DeviceAComboBox.ItemsSource = devicesA;
                DeviceBComboBox.ItemsSource = devicesB;

                if (devicesA.Count > 0) DeviceAComboBox.SelectedIndex = 0;
                if (devicesB.Count > 1) DeviceBComboBox.SelectedIndex = 1;
                else if (devicesB.Count > 0) DeviceBComboBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load audio hardware: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void OnWindowsVolumeChanged(AudioVolumeNotificationData data)
        {
            systemMasterVolume = data.MasterVolume;
            Dispatcher.Invoke(() =>
            {
                UpdateVolumeNodes();
            });
        }

        private void UpdateVolumeNodes()
        {
            string defaultId = defaultWindowsDevice?.ID;

            if (volumeNodeA != null && DeviceAComboBox.SelectedItem is AudioDevice selectedA)
            {
                float baseVolA = (float)(SliderVolA.Value / 100.0);
                volumeNodeA.Volume = (selectedA.Device.ID == defaultId) ? baseVolA : baseVolA * systemMasterVolume;
            }

            if (volumeNodeB != null && DeviceBComboBox.SelectedItem is AudioDevice selectedB)
            {
                float baseVolB = (float)(SliderVolB.Value / 100.0);
                volumeNodeB.Volume = (selectedB.Device.ID == defaultId) ? baseVolB : baseVolB * systemMasterVolume;
            }
        }

        private void SliderA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DelayAText != null) DelayAText.Text = $"{e.NewValue:0} ms";
        }

        private void SliderB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DelayBText != null) DelayBText.Text = $"{e.NewValue:0} ms";
        }

        private void SliderVolA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolAText != null) VolAText.Text = $"{e.NewValue:0} %";
            UpdateVolumeNodes(); // Recalculate using the new slider value
        }

        private void SliderVolB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolBText != null) VolBText.Text = $"{e.NewValue:0} %";
            UpdateVolumeNodes();
        }

        
        private int CalculateDelayBytes(int milliseconds, WaveFormat format)
        {
            double bytesPerMillisecond = format.AverageBytesPerSecond / 1000.0;
            int rawBytes = (int)(bytesPerMillisecond * milliseconds);
            return rawBytes - (rawBytes % format.BlockAlign);
        }

        // --- The Core Engine ---
        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                var selectedA = DeviceAComboBox.SelectedItem as AudioDevice;
                var selectedB = DeviceBComboBox.SelectedItem as AudioDevice;

                if (selectedA == null || selectedB == null || selectedA.Device.ID == selectedB.Device.ID)
                {
                    MessageBox.Show("Please select two distinct audio devices.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    defaultWindowsDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // Grab current Windows volume and start listening for changes
                    systemMasterVolume = defaultWindowsDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                    defaultWindowsDevice.AudioEndpointVolume.OnVolumeNotification += OnWindowsVolumeChanged;

                    systemCapture = new WasapiLoopbackCapture();

                    bufferA = new BufferedWaveProvider(systemCapture.WaveFormat) { DiscardOnBufferOverflow = true };
                    bufferB = new BufferedWaveProvider(systemCapture.WaveFormat) { DiscardOnBufferOverflow = true };

                    volumeNodeA = new VolumeSampleProvider(bufferA.ToSampleProvider());
                    volumeNodeB = new VolumeSampleProvider(bufferB.ToSampleProvider());

                    // Apply the initial volume calculations
                    UpdateVolumeNodes();

                    outputDeviceA = new WasapiOut(selectedA.Device, AudioClientShareMode.Shared, false, 50);
                    outputDeviceB = new WasapiOut(selectedB.Device, AudioClientShareMode.Shared, false, 50);

                    outputDeviceA.Init(volumeNodeA);
                    outputDeviceB.Init(volumeNodeB);

                    int delayMsA = (int)SliderA.Value;
                    int delayMsB = (int)SliderB.Value;

                    if (delayMsA > 0)
                    {
                        int delayBytesA = CalculateDelayBytes(delayMsA, systemCapture.WaveFormat);
                        bufferA.AddSamples(new byte[delayBytesA], 0, delayBytesA);
                    }

                    if (delayMsB > 0)
                    {
                        int delayBytesB = CalculateDelayBytes(delayMsB, systemCapture.WaveFormat);
                        bufferB.AddSamples(new byte[delayBytesB], 0, delayBytesB);
                    }

                    systemCapture.DataAvailable += (s, args) =>
                    {
                        bufferA.AddSamples(args.Buffer, 0, args.BytesRecorded);
                        bufferB.AddSamples(args.Buffer, 0, args.BytesRecorded);
                    };

                    outputDeviceA.Play();
                    outputDeviceB.Play();
                    systemCapture.StartRecording();

                    isRunning = true;
                    StartStopButton.Content = "STOP SYNC ENGINE";
                    StartStopButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 46, 99));
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Engine failed to start: {ex.Message}", "Audio Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StopEngine();
                }
            }
            else
            {
                StopEngine();
                isRunning = false;
                StartStopButton.Content = "START SYNC ENGINE";
                StartStopButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 173, 181));
            }
        }

        private void StopEngine()
        {
            // CRITICAl
            if (defaultWindowsDevice != null)
            {
                defaultWindowsDevice.AudioEndpointVolume.OnVolumeNotification -= OnWindowsVolumeChanged;
                defaultWindowsDevice.Dispose();
                defaultWindowsDevice = null;
            }

            systemCapture?.StopRecording();
            systemCapture?.Dispose();
            systemCapture = null;

            outputDeviceA?.Stop();
            outputDeviceA?.Dispose();
            outputDeviceA = null;

            outputDeviceB?.Stop();
            outputDeviceB?.Dispose();
            outputDeviceB = null;

            bufferA = null;
            bufferB = null;
            volumeNodeA = null;
            volumeNodeB = null;
        }
    }

    public class AudioDevice
    {
        public string Name { get; set; }
        public MMDevice Device { get; set; }
    }
}