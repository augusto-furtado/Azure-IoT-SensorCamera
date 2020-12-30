using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Shared;
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using System.Security.Cryptography;

namespace SensorCamera
{

    //This class is a wrapper which receives the necessary enrollment info to provision a device using DPS 
    class ProvisioningDeviceClientWrapper
    {

        // Global DPS endpoint
        private const string GlobalDeviceEndpoint = "global.azure-devices-provisioning.net";

        ProvisioningDeviceClient _provClient;
        SecurityProvider _security;

        public ProvisioningDeviceClientWrapper(string s_idScope, string registrationId, string enrollmentGroupPrimaryKey, string enrollmentGroupSecondaryKey)
        {
            //Group enrollment flow, the primary and secondary keys are derived from the enrollment group keys and from the desired registration id
            string primaryKey = ComputeDerivedSymmetricKey(Convert.FromBase64String(enrollmentGroupPrimaryKey), registrationId);
            string secondaryKey = ComputeDerivedSymmetricKey(Convert.FromBase64String(enrollmentGroupSecondaryKey), registrationId);

            SecurityProviderSymmetricKey security = new SecurityProviderSymmetricKey(registrationId, primaryKey, secondaryKey);
            ProvisioningTransportHandlerAmqp transport = new ProvisioningTransportHandlerAmqp(TransportFallbackType.TcpOnly);

            ProvisioningDeviceClient provClient = ProvisioningDeviceClient.Create(GlobalDeviceEndpoint, s_idScope, security, transport);


            _provClient = provClient;
            _security = security;
        }

        public async Task<DeviceClient> RunAsync()
        {
            Console.WriteLine($"RegistrationID = {_security.GetRegistrationID()}");
            VerifyRegistrationIdFormat(_security.GetRegistrationID());

            Console.Write("ProvisioningClient RegisterAsync . . . ");
            DeviceRegistrationResult result = await _provClient.RegisterAsync().ConfigureAwait(false);

            Console.WriteLine($"{result.Status}");
            Console.WriteLine($"ProvisioningClient AssignedHub: {result.AssignedHub}; DeviceID: {result.DeviceId}");

            if (result.Status != ProvisioningRegistrationStatusType.Assigned) return null;

            IAuthenticationMethod auth;
            if (_security is SecurityProviderTpm)
            {
                Console.WriteLine("Creating TPM DeviceClient authentication.");
                auth = new DeviceAuthenticationWithTpm(result.DeviceId, _security as SecurityProviderTpm);
            }
            else if (_security is SecurityProviderX509)
            {
                Console.WriteLine("Creating X509 DeviceClient authentication.");
                auth = new DeviceAuthenticationWithX509Certificate(result.DeviceId, (_security as SecurityProviderX509).GetAuthenticationCertificate());
            }
            else if (_security is SecurityProviderSymmetricKey)
            {
                Console.WriteLine("Creating Symmetric Key DeviceClient authenication");
                auth = new DeviceAuthenticationWithRegistrySymmetricKey(result.DeviceId, (_security as SecurityProviderSymmetricKey).GetPrimaryKey());
            }
            else
            {
                throw new NotSupportedException("Unknown authentication type.");
            }

            DeviceClient iotClient = DeviceClient.Create(result.AssignedHub, auth, TransportType.Amqp);

            return iotClient;
        }

        private void VerifyRegistrationIdFormat(string v)
        {
            var r = new Regex("^[a-z0-9-]*$");
            if (!r.IsMatch(v))
            {
                throw new FormatException("Invalid registrationId: The registration ID is alphanumeric, lowercase, and may contain hyphens");
            }
        }

        /// <summary>
        /// Generate the derived symmetric key for the provisioned device from the enrollment group symmetric key used in attestation
        /// </summary>
        /// <param name="masterKey">Symmetric key enrollment group primary/secondary key value</param>
        /// <param name="registrationId">the registration id to create</param>
        /// <returns>the primary/secondary key for the member of the enrollment group</returns>
        public static string ComputeDerivedSymmetricKey(byte[] masterKey, string registrationId)
        {
            using (var hmac = new HMACSHA256(masterKey))
            {
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(registrationId)));
            }
        }
    }
}
