// --------------------------------------------------------------------------------------------------------------------
// <copyright file="FaceTrackingViewer.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace FaceTrackingUdpSender
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using Microsoft.Kinect;
    using Microsoft.Kinect.Toolkit.FaceTracking;

    using System.Net;
    using System.Net.Sockets;
    using System.Text;

    using Point = System.Windows.Point;

    /// <summary>
    /// Class that uses the Face Tracking SDK to display a face mask for
    /// tracked skeletons
    /// </summary>
    public partial class FaceTrackingSender : UserControl, IDisposable
    {
        public static readonly DependencyProperty KinectProperty = DependencyProperty.Register(
            "Kinect", 
            typeof(KinectSensor), 
            typeof(FaceTrackingSender), 
            new PropertyMetadata(
                null, (o, args) => ((FaceTrackingSender)o).OnSensorChanged((KinectSensor)args.OldValue, (KinectSensor)args.NewValue)));

        private const uint MaxMissedFrames = 100;

        private readonly Dictionary<int, SkeletonFaceTracker> trackedSkeletons = new Dictionary<int, SkeletonFaceTracker>();

        private byte[] colorImage;

        private ColorImageFormat colorImageFormat = ColorImageFormat.Undefined;

        private short[] depthImage;

        private DepthImageFormat depthImageFormat = DepthImageFormat.Undefined;

        private bool disposed;

        private Skeleton[] skeletonData;

        private Socket sending_socket;
        private IPAddress send_to_address;
        private IPEndPoint sending_end_point;

        public bool renderMeshFlag = true;

        public FaceTrackingSender()
        {
            this.InitializeComponent();

            System.Diagnostics.Debug.WriteLine("Sizes:  ulong{0} float{1} short{2}", sizeof(ulong), sizeof(float), sizeof(ushort));

            sending_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sending_socket.DontFragment = true; //don't allow fragmentation of packets. throw an error if mtu is to small
            send_to_address = IPAddress.Parse("127.0.0.1"); //localhost
            //send_to_address = IPAddress.Parse("192.168.0.141"); //laptop
            
            //send_to_address = IPAddress.Parse("192.168.0.125"); //ofx computer
            sending_end_point = new IPEndPoint(send_to_address, 11001);
            //todo: Performance optimisation: find the right size for the byte[]. 
            //Consider http://stackoverflow.com/questions/1098897/what-is-the-largest-safe-udp-packet-size-on-the-internet
            //and http://en.wikipedia.org/wiki/Maximum_transmission_unit
        }

        ~FaceTrackingSender()
        {
            this.Dispose(false);
        }

        public KinectSensor Kinect
        {
            get
            {
                return (KinectSensor)this.GetValue(KinectProperty);
            }

            set
            {
                this.SetValue(KinectProperty, value);
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                this.ResetFaceTracking();

                this.disposed = true;
            }
        }

        public void SetIpAddress(IPAddress ip)
        {
            sending_end_point.Address = ip;
        }

        public void SetPort(int port)
        {
            sending_end_point.Port = port;
        }

        public void SetSendDestination(string ip, string port)
        {
            send_to_address = IPAddress.Parse(ip); //ofx computer
            int portValue;
            int.TryParse(port, out portValue);
            sending_end_point = new IPEndPoint(send_to_address, portValue);
            MessageBox.Show("Destination changed to " + sending_end_point.Address + " : " + sending_end_point.Port);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (!renderMeshFlag) return;
            foreach (SkeletonFaceTracker faceInformation in this.trackedSkeletons.Values)
            {
                faceInformation.DrawFaceModel(drawingContext);
            }
        }

        private void OnAllFramesReady(object sender, AllFramesReadyEventArgs allFramesReadyEventArgs)
        {
            ColorImageFrame colorImageFrame = null;
            DepthImageFrame depthImageFrame = null;
            SkeletonFrame skeletonFrame = null;

            try
            {
                colorImageFrame = allFramesReadyEventArgs.OpenColorImageFrame();
                depthImageFrame = allFramesReadyEventArgs.OpenDepthImageFrame();
                skeletonFrame = allFramesReadyEventArgs.OpenSkeletonFrame();

                if (colorImageFrame == null || depthImageFrame == null || skeletonFrame == null)
                {
                    return;
                }

                // Check for image format changes.  The FaceTracker doesn't
                // deal with that so we need to reset.
                if (this.depthImageFormat != depthImageFrame.Format)
                {
                    this.ResetFaceTracking();
                    this.depthImage = null;
                    this.depthImageFormat = depthImageFrame.Format;
                }

                if (this.colorImageFormat != colorImageFrame.Format)
                {
                    this.ResetFaceTracking();
                    this.colorImage = null;
                    this.colorImageFormat = colorImageFrame.Format;
                }

                // Create any buffers to store copies of the data we work with
                if (this.depthImage == null)
                {
                    this.depthImage = new short[depthImageFrame.PixelDataLength];
                }

                if (this.colorImage == null)
                {
                    this.colorImage = new byte[colorImageFrame.PixelDataLength];
                }
                
                // Get the skeleton information
                if (this.skeletonData == null || this.skeletonData.Length != skeletonFrame.SkeletonArrayLength)
                {
                    this.skeletonData = new Skeleton[skeletonFrame.SkeletonArrayLength];
                }

                colorImageFrame.CopyPixelDataTo(this.colorImage);
                depthImageFrame.CopyPixelDataTo(this.depthImage);
                skeletonFrame.CopySkeletonDataTo(this.skeletonData);

                // Update the list of trackers and the trackers with the current frame information
                foreach (Skeleton skeleton in this.skeletonData)
                {
                    if (skeleton.TrackingState == SkeletonTrackingState.Tracked
                        || skeleton.TrackingState == SkeletonTrackingState.PositionOnly)
                    {
                        // We want keep a record of any skeleton, tracked or untracked.
                        if (!this.trackedSkeletons.ContainsKey(skeleton.TrackingId))
                        {
                            this.trackedSkeletons.Add(skeleton.TrackingId, new SkeletonFaceTracker(sending_socket, sending_end_point));
                        }

                        // Give each tracker the upated frame.
                        SkeletonFaceTracker skeletonFaceTracker;
                        if (this.trackedSkeletons.TryGetValue(skeleton.TrackingId, out skeletonFaceTracker))
                        {
                            skeletonFaceTracker.OnFrameReady(this.Kinect, colorImageFormat, colorImage, depthImageFormat, depthImage, skeleton);
                            skeletonFaceTracker.LastTrackedFrame = skeletonFrame.FrameNumber;
                        }
                    }
                }

                this.RemoveOldTrackers(skeletonFrame.FrameNumber);

                this.InvalidateVisual();
            }
            finally
            {
                if (colorImageFrame != null)
                {
                    colorImageFrame.Dispose();
                }

                if (depthImageFrame != null)
                {
                    depthImageFrame.Dispose();
                }

                if (skeletonFrame != null)
                {
                    skeletonFrame.Dispose();
                }
            }
        }

        private void OnSensorChanged(KinectSensor oldSensor, KinectSensor newSensor)
        {
            if (oldSensor != null)
            {
                oldSensor.AllFramesReady -= this.OnAllFramesReady;
                this.ResetFaceTracking();
            }

            if (newSensor != null)
            {
                newSensor.AllFramesReady += this.OnAllFramesReady;
            }
        }

        /// <summary>
        /// Clear out any trackers for skeletons we haven't heard from for a while
        /// </summary>
        private void RemoveOldTrackers(int currentFrameNumber)
        {
            var trackersToRemove = new List<int>();

            foreach (var tracker in this.trackedSkeletons)
            {
                uint missedFrames = (uint)currentFrameNumber - (uint)tracker.Value.LastTrackedFrame;
                if (missedFrames > MaxMissedFrames)
                {
                    // There have been too many frames since we last saw this skeleton
                    trackersToRemove.Add(tracker.Key);
                }
            }

            foreach (int trackingId in trackersToRemove)
            {
                this.RemoveTracker(trackingId);
            }
        }

        private void RemoveTracker(int trackingId)
        {
            this.trackedSkeletons[trackingId].Dispose();
            this.trackedSkeletons.Remove(trackingId);
        }

        private void ResetFaceTracking()
        {
            foreach (int trackingId in new List<int>(this.trackedSkeletons.Keys))
            {
                this.RemoveTracker(trackingId);
            }
        }

        private class SkeletonFaceTracker : IDisposable
        {
            private Socket sending_socket;
            private IPEndPoint sending_end_point;
            public SkeletonFaceTracker(Socket sending_socket, IPEndPoint sending_end_point) : base()
            {
                this.sending_socket = sending_socket;
                this.sending_end_point = sending_end_point;
            }

            private static FaceTriangle[] faceTriangles;

            private EnumIndexableCollection<FeaturePoint, PointF> projectedShapePoints;
            EnumIndexableCollection<FeaturePoint, Vector3DF> shapePoints;

            private FaceTracker faceTracker;

            public bool lastFaceTrackSucceeded { get; private set; }
            

            private SkeletonTrackingState skeletonTrackingState;

            public int LastTrackedFrame { get; set; }

            public void Dispose()
            {
                if (this.faceTracker != null)
                {
                    this.faceTracker.Dispose();
                    this.faceTracker = null;
                }
            }

            public void DrawFaceModel(DrawingContext drawingContext)
            {
                if (!this.lastFaceTrackSucceeded || this.skeletonTrackingState != SkeletonTrackingState.Tracked)
                {
                    return;
                }

                var faceModelPts = new List<Point>();
                var faceModel = new List<FaceModelTriangle>();

                for (int i = 0; i < this.projectedShapePoints.Count; i++)
                {
                    faceModelPts.Add(new Point(this.projectedShapePoints[i].X + 0.5f, this.projectedShapePoints[i].Y + 0.5f));
                }

                foreach (var t in faceTriangles)
                {
                    var triangle = new FaceModelTriangle();
                    triangle.P1 = faceModelPts[t.First];
                    triangle.P2 = faceModelPts[t.Second];
                    triangle.P3 = faceModelPts[t.Third];
                    faceModel.Add(triangle);
                }

                var faceModelGroup = new GeometryGroup();
                for (int i = 0; i < faceModel.Count; i++)
                {
                    var faceTriangle = new GeometryGroup();
                    faceTriangle.Children.Add(new LineGeometry(faceModel[i].P1, faceModel[i].P2));
                    faceTriangle.Children.Add(new LineGeometry(faceModel[i].P2, faceModel[i].P3));
                    faceTriangle.Children.Add(new LineGeometry(faceModel[i].P3, faceModel[i].P1));
                    faceModelGroup.Children.Add(faceTriangle);
                }

                drawingContext.DrawGeometry(Brushes.LightYellow, new Pen(Brushes.LightYellow, 1.0), faceModelGroup);
            }

            private ulong tick = 0;
            private byte[] buffer;
            private void initByteBuffer(){
                int header = sizeof(ulong) + sizeof(ushort); //ulong for time ticks, ushort for number of faces in this frame
                int faceHeader = sizeof(ushort) + sizeof(uint); //ushort for faceID, uint for num facePoints
                int bufferLength = header + //faceID + number of facePoints
                                         faceHeader + //only allow one face for now
                    //sizeof(float) * 2 * this.projectedShapePoints.Count //todo: this.shapePoints instead of projected
                                         sizeof(float) * 3 * 121;// this.shapePoints.Count;
                //Debug.WriteLine("buffer Length: " + bufferLength);
                buffer = new byte[bufferLength];
            }
            public byte[] WriteFaceDataToBuffer()
            {
                if (buffer == null)
                {
                    System.Diagnostics.Debug.WriteLine("BUFFER INITIALISED COS NULL");
                    initByteBuffer();
                }
                tick++;
                Array.Copy(System.BitConverter.GetBytes(tick), 0, buffer, 0, sizeof(ulong)); //write ticks (what a nonsense thing to do...)
                Array.Copy(System.BitConverter.GetBytes((ushort)1), 0, buffer, sizeof(ulong), sizeof(ushort)); //write number of faces (only 1 at the moment)
                //Debug.WriteLine("buffer 4 and 5: {0}  {1}", buffer[0], buffer[3]);
                //Face Header
                int faceHeaderIndex = sizeof(ulong) + sizeof(ushort); //header; //since only one face is supported this is easy...
                Array.Copy(System.BitConverter.GetBytes((ushort)42), 0, buffer, faceHeaderIndex, sizeof(ushort)); //write the faceID (allways 42 at the moment)
                Array.Copy(System.BitConverter.GetBytes(this.shapePoints.Count), 0, buffer, faceHeaderIndex + sizeof(ushort), sizeof(int)); //write the number of points in this face

                int facePointsIndex = faceHeaderIndex + sizeof(ushort) + sizeof(uint);//faceHeader;

                //Debug.WriteLine(facePoints.Count); //allways 121?
                /*
                int i = 0;
                foreach (var sp in shapePoints)
                {
                    Array.Copy(System.BitConverter.GetBytes(sp.X), 0, buffer, facePointsIndex + i * sizeof(float) * 3, sizeof(float));
                    Array.Copy(System.BitConverter.GetBytes(sp.Y), 0, buffer, facePointsIndex + i * sizeof(float) * 3 + sizeof(float), sizeof(float));
                    Array.Copy(System.BitConverter.GetBytes(sp.Z), 0, buffer, facePointsIndex + i * sizeof(float) * 3 + sizeof(float) * 2, sizeof(float));
                    i++;
                }
                */
                for (int i = 0; i < this.shapePoints.Count; i++)
                {
                    Array.Copy(System.BitConverter.GetBytes(this.shapePoints[i].X), 0, buffer, facePointsIndex + i * sizeof(float) * 3, sizeof(float));
                    Array.Copy(System.BitConverter.GetBytes(this.shapePoints[i].Y), 0, buffer, facePointsIndex + i * sizeof(float) * 3 + sizeof(float), sizeof(float));
                    Array.Copy(System.BitConverter.GetBytes(this.shapePoints[i].Z), 0, buffer, facePointsIndex + i * sizeof(float) * 3 + sizeof(float) * 2, sizeof(float));
                }

                //Debug.WriteLine("shape point 0: {0} {1} {2}", this.shapePoints[0].X, this.shapePoints[0].Y, this.shapePoints[0].Z);

                return buffer;
            }

            /// <summary>
            /// Updates the face tracking information for this skeleton
            /// </summary>
            internal void OnFrameReady(KinectSensor kinectSensor, ColorImageFormat colorImageFormat, byte[] colorImage, DepthImageFormat depthImageFormat, short[] depthImage, Skeleton skeletonOfInterest)
            {
                this.skeletonTrackingState = skeletonOfInterest.TrackingState;

                if (this.skeletonTrackingState != SkeletonTrackingState.Tracked)
                {
                    // nothing to do with an untracked skeleton.
                    return;
                }

                if (this.faceTracker == null)
                {
                    try
                    {
                        this.faceTracker = new FaceTracker(kinectSensor);
                    }
                    catch (InvalidOperationException)
                    {
                        // During some shutdown scenarios the FaceTracker
                        // is unable to be instantiated.  Catch that exception
                        // and don't track a face.
                        Debug.WriteLine("AllFramesReady - creating a new FaceTracker threw an InvalidOperationException");
                        this.faceTracker = null;
                    }
                }

                if (this.faceTracker != null)
                {
                    FaceTrackFrame frame = this.faceTracker.Track(
                        colorImageFormat, colorImage, depthImageFormat, depthImage, skeletonOfInterest);

                    this.lastFaceTrackSucceeded = frame.TrackSuccessful;
                    if (this.lastFaceTrackSucceeded)
                    {
                        
                        if (faceTriangles == null)
                        {
                            // only need to get this once.  It doesn't change.
                            faceTriangles = frame.GetTriangles();
                            initByteBuffer();
                        }
                        /*
hurra, dont need 4th vertex
hurra, dont need 35th vertex
hurra, dont need 36th vertex
hurra, dont need 37th vertex
hurra, dont need 38th vertex
hurra, dont need 39th vertex
hurra, dont need 41th vertex
hurra, dont need 42th vertex
hurra, dont need 43th vertex
                        */

                        this.projectedShapePoints = frame.GetProjected3DShape();
                        this.shapePoints = frame.Get3DShape();
                        
                        byte[] buffer = WriteFaceDataToBuffer();
                        //System.Diagnostics.Debug.WriteLine("Should send {0}", buffer.Length);//1468
                        
                        //need to reduce by 444 bytes
                        //a point is 12. 85 points would result in 1020 bytes. 4 bytes remaining for face id...?
                        //currently 121 points. which ones can be leaft out?
                        //found 9 not needed. still 27 to much...
                        //header is useless. 434 bytes remaining after removing it
                        //byte[] buffer = buffer = new byte[1024];//works fast, even if unknown send to ip..
                        //ushort thefaceid = 22;
                        //Array.Copy(System.BitConverter.GetBytes(thefaceid), 0, buffer, 0, sizeof(ushort));
                        try
                        {
                            sending_socket.SendTo(buffer, sending_end_point); // is this blocking? need to start sending asynchronously?!?

                            //System.Diagnostics.Debug.WriteLine("sending {0} bytes to ip {1} on port {2}", buffer.Length, sending_end_point.Address, sending_end_point.Port);
                            //Console.WriteLine( buffer);
                        }
                        catch (Exception send_exception)
                        {
                            System.Diagnostics.Debug.WriteLine("Exception {0}", send_exception.Message);
                            //System.Diagnostics.Debug.WriteLine("Is the buffer with it's {0} bytes to long to send in one packet?", buffer.Length);
                        }
                    }
                }
            }

            private struct FaceModelTriangle
            {
                public Point P1;
                public Point P2;
                public Point P3;
            }
        }
    }
}