// -----------------------------------------------------------------------
// <copyright file="MainWindow.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace FaceTrackingUdpSender
{
    using System;
    using System.Windows;
    using System.Windows.Data;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using Microsoft.Kinect;
    using Microsoft.Kinect.Toolkit;
    using System.Net;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly int Bgr32BytesPerPixel = (PixelFormats.Bgr32.BitsPerPixel + 7) / 8;
        private readonly KinectSensorChooser sensorChooser = new KinectSensorChooser();
        private WriteableBitmap colorImageWritableBitmap;
        private byte[] colorImageData;
        private ColorImageFormat currentColorImageFormat = ColorImageFormat.Undefined;

        public MainWindow()
        {
            InitializeComponent();

            var faceTrackingViewerBinding = new Binding("Kinect") { Source = sensorChooser };
            faceTrackingViewer.SetBinding(FaceTrackingSender.KinectProperty, faceTrackingViewerBinding);

            sensorChooser.KinectChanged += SensorChooserOnKinectChanged;

            sensorChooser.Start();
        }

        private void SensorChooserOnKinectChanged(object sender, KinectChangedEventArgs kinectChangedEventArgs)
        {
            KinectSensor oldSensor = kinectChangedEventArgs.OldSensor;
            KinectSensor newSensor = kinectChangedEventArgs.NewSensor;

            if (oldSensor != null)
            {
                oldSensor.AllFramesReady -= KinectSensorOnAllFramesReady;
                oldSensor.ColorStream.Disable();
                oldSensor.DepthStream.Disable();
                oldSensor.DepthStream.Range = DepthRange.Default;
                oldSensor.SkeletonStream.Disable();
                oldSensor.SkeletonStream.EnableTrackingInNearRange = false;
                oldSensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Default;
            }

            if (newSensor != null)
            {
                try
                {
                    newSensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
                    newSensor.DepthStream.Enable(DepthImageFormat.Resolution320x240Fps30);
                    try
                    {
                        // This will throw on non Kinect For Windows devices.
                        newSensor.DepthStream.Range = DepthRange.Near;
                        newSensor.SkeletonStream.EnableTrackingInNearRange = true;
                    }
                    catch (InvalidOperationException)
                    {
                        newSensor.DepthStream.Range = DepthRange.Default;
                        newSensor.SkeletonStream.EnableTrackingInNearRange = false;
                    }

                    newSensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Seated;
                    newSensor.SkeletonStream.Enable();
                    newSensor.AllFramesReady += KinectSensorOnAllFramesReady;
                }
                catch (InvalidOperationException)
                {
                    // This exception can be thrown when we are trying to
                    // enable streams on a device that has gone away.  This
                    // can occur, say, in app shutdown scenarios when the sensor
                    // goes away between the time it changed status and the
                    // time we get the sensor changed notification.
                    //
                    // Behavior here is to just eat the exception and assume
                    // another notification will come along if a sensor
                    // comes back.
                }
            }
        }

        private void WindowClosed(object sender, EventArgs e)
        {
            sensorChooser.Stop();
            faceTrackingViewer.Dispose();
        }

        private void KinectSensorOnAllFramesReady(object sender, AllFramesReadyEventArgs allFramesReadyEventArgs)
        {
            using (var colorImageFrame = allFramesReadyEventArgs.OpenColorImageFrame())
            {
                if (colorImageFrame == null)
                {
                    return;
                }

                // Make a copy of the color frame for displaying.
                var haveNewFormat = this.currentColorImageFormat != colorImageFrame.Format;
                if (haveNewFormat)
                {
                    this.currentColorImageFormat = colorImageFrame.Format;
                    this.colorImageData = new byte[colorImageFrame.PixelDataLength];
                    this.colorImageWritableBitmap = new WriteableBitmap(
                        colorImageFrame.Width, colorImageFrame.Height, 96, 96, PixelFormats.Bgr32, null);
                    ColorImage.Source = this.colorImageWritableBitmap;
                }

                colorImageFrame.CopyPixelDataTo(this.colorImageData);
                this.colorImageWritableBitmap.WritePixels(
                    new Int32Rect(0, 0, colorImageFrame.Width, colorImageFrame.Height),
                    this.colorImageData,
                    colorImageFrame.Width * Bgr32BytesPerPixel,
                    0);
            }
        }

        private void renderRGBCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("render rgb checke");
            if (sensorChooser.Kinect != null)
            {
                System.Diagnostics.Debug.WriteLine("adding onAllFramesReady. rgb should be drawn");
                sensorChooser.Kinect.AllFramesReady += KinectSensorOnAllFramesReady;
            }
        }

        private void renderRGBCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("render rgb unchecked");
            if (sensorChooser.Kinect != null)
            {
                System.Diagnostics.Debug.WriteLine("removing onAllFramesReady. rgb should no longer be drawn");
                sensorChooser.Kinect.AllFramesReady -= KinectSensorOnAllFramesReady;
            }
        }

        private void renderWireCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("render wire checked");
            faceTrackingViewer.renderMeshFlag = true;
        }

        private void renderWireCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("render wire unchecked");
            faceTrackingViewer.renderMeshFlag = false;
        }
        
        private void ipTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            try
            {
                IPAddress ip = IPAddress.Parse(ipTextBox.Text);
                ipTextBox.Foreground = this.Resources["KinectPurpleBrush"] as SolidColorBrush;
                System.Diagnostics.Debug.WriteLine("ip changed to " + ipTextBox.Text);
                faceTrackingViewer.SetIpAddress(ip);
            }
            catch
            {
                ipTextBox.Foreground = this.Resources["WrongValueBrush"] as SolidColorBrush;
                System.Diagnostics.Debug.WriteLine("ip not ok");
            }
            
        }

        private void portTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            int portValue;
            if (int.TryParse(portTextBox.Text, out portValue))
            {
                portTextBox.Foreground = this.Resources["KinectPurpleBrush"] as SolidColorBrush;
                System.Diagnostics.Debug.WriteLine("port changed to " + portTextBox.Text);
            }
            else
            {
                portTextBox.Foreground = this.Resources["WrongValueBrush"] as SolidColorBrush;
                System.Diagnostics.Debug.WriteLine("port not ok");
            }
            
        }

    }
}
