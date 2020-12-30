using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Client;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using MMALSharp;
using MMALSharp.Common;
using MMALSharp.Components;
using MMALSharp.Handlers;
using MMALSharp.Ports;
using MMALSharp.Ports.Outputs;
using System.Threading;

namespace SensorCamera
{
    class Program
    {
        //DPS Scope ID
        private static string idScope;
        //keys from DPS enrollment group. 
        private static string enrollmentGroupPrimaryKey;
        private static string enrollmentGroupSecondaryKey;
        //Registration Id for this Device - required if using DPS
        private static string registrationId;
        //Device String Connection - Use this or DPS info above to connect the device - add GatewayHostName at the end if a downstream device using a Edge Gateway
        private static string deviceStringConnection;
        //path to save picture taken
        private static string pathToSavePicture;

        private static DeviceClient deviceClient = null;
        private static string[] config = null;
        private static MMALCamera cam;

        static void Main(string[] args)
        {
            //load app properties 
            ReadConfigFile();
            idScope = GetProperty("idScope");
            enrollmentGroupPrimaryKey = GetProperty("enrollmentGroupPrimaryKey");
            enrollmentGroupSecondaryKey = GetProperty("enrollmentGroupSecondaryKey");
            registrationId = GetProperty("registrationId");
            deviceStringConnection = GetProperty("deviceStringConnection");
            pathToSavePicture = GetProperty("pathToSavePicture");

            bool dpsInfoOk = !string.IsNullOrWhiteSpace(idScope) && !string.IsNullOrWhiteSpace(enrollmentGroupPrimaryKey) &&
                 !string.IsNullOrWhiteSpace(enrollmentGroupSecondaryKey) && !string.IsNullOrWhiteSpace(registrationId);
            bool directConnection = !string.IsNullOrWhiteSpace(deviceStringConnection);

            if (!(dpsInfoOk || directConnection))
            {
                Console.WriteLine("ID Scope, Keys and Registration ID must be provided if using DPS, otherwise fill the Device Connection String");
                Console.ReadLine();
            }
            else
            {
                //connect device directly if device string connection is known
                if (!string.IsNullOrWhiteSpace(deviceStringConnection))
                {
                    //this install a root certificate in the OS - applicable if you using this device as a downstream to connect through a Edge Gateway. 
                    if (deviceStringConnection.Contains("GatewayHostName"))
                    {
                        InstallCACert();
                    }

                    deviceClient = DeviceClient.CreateFromConnectionString(deviceStringConnection, TransportType.Mqtt_Tcp_Only);
                }
                else
                {
                    //Provision through DPS and return DeviceClient object
                    ProvisioningDeviceClientWrapper provisionWrapper = new ProvisioningDeviceClientWrapper(
                        idScope, registrationId, enrollmentGroupPrimaryKey, enrollmentGroupSecondaryKey);

                    deviceClient = provisionWrapper.RunAsync().GetAwaiter().GetResult();
                }
                
                // Create a handler for the direct method call
                deviceClient.SetMethodHandlerAsync("TakePicture", TakePicture, null).Wait();

                //Initiate camera (singleton pattern) - must be executed just once - Attention - while this app is running the camera can´t be used by another program
                cam = MMALCamera.Instance;

                //open device connection
                deviceClient.OpenAsync().ConfigureAwait(false);

                //execute this method to program continue running
                Console.ReadLine();

                //close device connection
                deviceClient.CloseAsync().ConfigureAwait(false);

                // Only call when you no longer require the camera, i.e. on app shutdown.
                cam.Cleanup();
            }

        }
               
        private static async void SendTelemetryData()
        {
            if (deviceClient != null)
            {
                string payload = "newImage";
                Message message = new Message(Encoding.UTF8.GetBytes(payload));
                await deviceClient.SendEventAsync(message).ConfigureAwait(false);

                Console.WriteLine("Message sent from " + registrationId + " is: " + payload);
            }
        }

        // Handle the direct method call - this method take a picture using the Camera and send notification to IoT Hub
        private static Task<MethodResponse> TakePicture(MethodRequest methodRequest, object userContext)
        {
            try
            {
                var data = Encoding.UTF8.GetString(methodRequest.Data);

                if (!string.IsNullOrWhiteSpace(data) && !(data == "null"))
                {
                    // Remove quotes from data, if any
                    data = data.Replace("\"", "");

                    pathToSavePicture = data;
                    Console.WriteLine("new path to save picture is: " + pathToSavePicture);
                }

                TakePictureAndSave();
                SendTelemetryData();

                // Acknowledge the direct method call with a 200 success message.
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            catch (Exception exception)
            {
                // Acknowledge the direct method call with a 400 error message.
                string result = "{\"result\":\"Exception: " + exception.ToString() + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }

        }

        private static void TakePictureAndSave()
        {
            using (var imgCaptureHandler = new ImageStreamCaptureHandler(pathToSavePicture + "/", "jpg"))
            using (var imgEncoder = new MMALImageEncoder())
            using (var nullSink = new MMALNullSinkComponent())
            {
                cam.ConfigureCameraSettings();

                var portConfig = new MMALPortConfig(MMALEncoding.JPEG, MMALEncoding. I420, 90);

                // Create our component pipeline.         
                imgEncoder.ConfigureOutputPort(portConfig, imgCaptureHandler);

                cam.Camera.StillPort.ConnectTo(imgEncoder);
                cam.Camera.PreviewPort.ConnectTo(nullSink);

                // Camera warm up time
                Thread.Sleep(2000);
                cam.ProcessAsync(cam.Camera.StillPort);
                Thread.Sleep(2000);
            }
        }

        /// <summary>
        /// Add certificate in local cert store (at your OS) for use by downstream device
        /// client for secure connection to IoT Edge runtime.
        ///
        ///    Note: On Windows machines, if you have not run this from an Administrator prompt,
        ///    a prompt will likely come up to confirm the installation of the certificate.
        ///    This usually happens the first time a certificate will be installed.
        /// </summary>
        static void InstallCACert()
        {
            string trustedCACertPath = "azure-iot-test-only.root.ca.cert.pem";
            if (!string.IsNullOrWhiteSpace(trustedCACertPath))
            {
                Console.WriteLine("User configured CA certificate path: {0}", trustedCACertPath);
                if (!File.Exists(trustedCACertPath))
                {
                    // cannot proceed further without a proper cert file
                    Console.WriteLine("Certificate file not found: {0}", trustedCACertPath);
                    throw new InvalidOperationException("Invalid certificate file.");
                }
                else
                {
                    Console.WriteLine("Attempting to install CA certificate: {0}", trustedCACertPath);
                    X509Store store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(new X509Certificate2(X509Certificate.CreateFromCertFile(trustedCACertPath)));
                    Console.WriteLine("Successfully added certificate: {0}", trustedCACertPath);
                    store.Close();
                }
            }
            else
            {
                Console.WriteLine("trustedCACertPath was not set or null, not installing any CA certificate");
            }
        }

        //The config file must have a property in each line and the key and value must be separated by " = "
        static void ReadConfigFile()
        {
            // Read each line of the file into a string array. Each element of the array is one line of the file.
            string[] lines = System.IO.File.ReadAllLines(@"AppProperties.conf");
            config = lines;
        }

        static string GetProperty(string property)
        {
            string separator = " = ";
            foreach (string line in config)
            {
                if (property == line.Split(separator)[0])
                    return line.Split(separator)[1];
            }
            return null;
        }
    }
}
