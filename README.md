SensorCamera
This app take a picture using a camera (OmniVision OV5647 - 5MP) attached to RaspberryPI and send notification to IoT Hub.


App architecture:
Projects created from template ".Net Core Console - C#"
App using Azure SDKs wich can connect to the Edge Device Gateway (being a downstream device) or can connect to the IoT Hub directly (DPS can be used to auto-provision).

References:
https://github.com/techyian/MMALSharp