using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Windows;

namespace BluetoothAudioHub
{
    public partial class MainWindow : Window
    {
        private bool isRunning = false;
        private WasapiLoopbackCapture systemCapture; // The "microphone" listening to your PC audio
        private WasapiOut outputDeviceA;             // The speaker pushing to Device A
        private WasapiOut outputDeviceB;             // The speaker pushing to Device B
        private BufferedWaveProvider bufferA;        // The memory holding Device A's audio chunks
        private BufferedWaveProvider bufferB;        // The memory holding Device B's audio chunks

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
                    // CRITICAL FIX: We wrap the extraction in its own try/catch.
                    // If a "ghost" device throws 0xE000020B, it fails gracefully and skips to the next item.
                    try
                    {
                        string statusIcon = endpoint.State == DeviceState.Active ? "🟢" : "🔴";
                        string displayName = $"{statusIcon} {endpoint.FriendlyName}";

                        devicesA.Add(new AudioDevice { Name = displayName, Device = endpoint });
                        devicesB.Add(new AudioDevice { Name = displayName, Device = endpoint });
                    }
                    catch (Exception)
                    {
                        // A corrupted device was found. Ignore it and continue the loop.
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
                MessageBox.Show(
                    $"Failed to load audio hardware: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                var selectedA = DeviceAComboBox.SelectedItem as AudioDevice;
                var selectedB = DeviceBComboBox.SelectedItem as AudioDevice;

                // Make sure the user picked valid devices that aren't the same
                if (selectedA == null || selectedB == null || selectedA.Device.ID == selectedB.Device.ID)
                {
                    MessageBox.Show("Please select two distinct audio devices.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    // 1. Initialize the Loopback Capture (Listens to default PC audio)
                    systemCapture = new WasapiLoopbackCapture();

                    // 2. Create the memory buffers using the exact audio quality the PC is currently using
                    bufferA = new BufferedWaveProvider(systemCapture.WaveFormat) { DiscardOnBufferOverflow = true };
                    bufferB = new BufferedWaveProvider(systemCapture.WaveFormat) { DiscardOnBufferOverflow = true };

                    // 3. Initialize the Outputs targeting the specific Bluetooth hardware IDs
                    outputDeviceA = new WasapiOut(selectedA.Device, AudioClientShareMode.Shared, false, 50);
                    outputDeviceB = new WasapiOut(selectedB.Device, AudioClientShareMode.Shared, false, 50);

                    outputDeviceA.Init(bufferA);
                    outputDeviceB.Init(bufferB);

                    // 4. THE CLONER: Every time the PC makes a sound, copy it to both buffers instantly
                    systemCapture.DataAvailable += (s, args) =>
                    {
                        bufferA.AddSamples(args.Buffer, 0, args.BytesRecorded);
                        bufferB.AddSamples(args.Buffer, 0, args.BytesRecorded);
                    };

                    // 5. Fire everything up!
                    outputDeviceA.Play();
                    outputDeviceB.Play();
                    systemCapture.StartRecording();

                    // Update UI
                    isRunning = true;
                    StartStopButton.Content = "STOP SYNC ENGINE";
                    StartStopButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 46, 99)); // Alert Pink
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Engine failed to start: {ex.Message}", "Audio Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StopEngine(); // Clean up if it crashed
                }
            }
            else
            {
                StopEngine();

                // Update UI
                isRunning = false;
                StartStopButton.Content = "START SYNC ENGINE";
                StartStopButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 173, 181)); // Teal
            }
        }

        // Helper method to safely shut down the audio streams
        private void StopEngine()
        {
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
        }
    }

    // Upgraded to hold the actual Windows Hardware Reference, not just the string name
    public class AudioDevice
    {
        public string Name { get; set; }
        public MMDevice Device { get; set; }
    }
}