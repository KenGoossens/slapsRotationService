using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Net.Http;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices;
using System.Text;
using System.Text.Json;
using System.IO;

namespace slapsWinService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly JsonManipulations jsonSerializer = new JsonManipulations();

        /**
         * Might need to be in another direct, currently in the current user
         **/
        private readonly string _registeryPath = @"SOFTWARE\SLAPS_Service";
        private readonly string _timeStampKey = "lastrun";
        private readonly string _azureFunctionsKeyWritePassword = "urlWrite";
        private readonly string _localAdminUserNameKey = "username";
        private readonly string _timeIntervalKey = "interval";
        private readonly string _slapsServiceName = "SLAPSService";
        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        private bool RegistryKeyExists(string path)
        {
            using RegistryKey regKey = Registry.CurrentUser.OpenSubKey(path);
            return regKey != null;
        }

        private bool RegistryValueExists(string path, string key)
        {
            return GetRegistryKeyValue(path, key) != null;
        }

        private string GetRegistryKeyValue(string path, string key)
        {
            if (!RegistryKeyExists(path))
            {
                return null;
  
            }
            RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(path);
            object keyValue = registryKey.GetValue(key);
            if (keyValue != null)
            {
                return keyValue.ToString();
            }

            return null;
                
        }

        private bool CreateRegistryKey(string path, bool isSensitive, string key = "")
        {
            if(RegistryKeyExists(path))
            {
                _logger.LogWarning(String.Format("{0}: {1} already exists in HKEY_CURRENT_USER. Skipping registry key creation", _slapsServiceName, path));
                
            } else
            {
                using RegistryKey regKey = Registry.CurrentUser.CreateSubKey(path, true);
               
                if (isSensitive)
                {
                    _logger.LogInformation(String.Format("{0}: created registry key at {1} at {2}", _slapsServiceName, "REDACTED", path));
                }
                else
                {
                    _logger.LogInformation(String.Format("{0}: created registry key at {1} at {2}", _slapsServiceName, key, path));
                }
                return true;
            }

            return false;
        }

        private bool WriteRegistryKey(string path, string key, bool isSensitive, string value = "")
        {
            if (!RegistryKeyExists(path))
            {
                _logger.LogError(String.Format("{0}: Cannot write key {1} with value {2} at {3}, because {3} does not exist.", _slapsServiceName, key, value, path));
            }
            else
            {
                using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(path, true);
                registryKey.SetValue(key, value);
                if (isSensitive)
                {
                    _logger.LogInformation(String.Format("{0}: wrote key {1} with value {2} at {3}", _slapsServiceName, key, "REDACTED", path));
                }
                else
                {
                    _logger.LogInformation(String.Format("{0}: wrote key {1} with value {2} at {3}", _slapsServiceName, key, value, path));
                }
                return true;
            }

            return false;
        }

        private DateTime ConvertEpochToDateTime(string timestamp)
        {
            long epochTimeLastRun = long.Parse(timestamp);
            return DateTimeOffset.FromUnixTimeMilliseconds(epochTimeLastRun).UtcDateTime;
        }

        private bool HasRunToday(string timestamp)
        {
            DateTime dateTimeLastRun = ConvertEpochToDateTime(timestamp);
            DateTime now = DateTime.Now.ToUniversalTime();


            return dateTimeLastRun > now.AddHours(-24) && dateTimeLastRun <= now;
        }

        private bool UserExists(string userName)
        {
            try
            {
                PrincipalContext context = new PrincipalContext(ContextType.Machine);
                UserPrincipal user = UserPrincipal.FindByIdentity(context, userName);
                return user != null;
            }
            catch (Exception ex)
            {
                //Log exception
                _logger.LogInformation(String.Format("{0}: Could not look for user {1} on machine {2}. Error message: {3}", _slapsServiceName, userName, Environment.MachineName, ex.Message));
            }


            return false;
        }

        private void ChangePassword(string userName, string oldPassword, string newPassword)
        {
            try
            {
                using var context = new PrincipalContext(ContextType.Machine);
                using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, userName);

                if(user != null)
                {
                    user.ChangePassword(oldPassword, newPassword);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(String.Format("{0}: Could not change password for user {1} on machine {2}. Error message: {3}", _slapsServiceName, userName, Environment.MachineName, ex.Message));
            }
        }

        public void AddUserToGroup(string userId, string groupName)
        {
            try
            {
                using PrincipalContext pc = new PrincipalContext(ContextType.Machine);
                GroupPrincipal group = GroupPrincipal.FindByIdentity(pc, groupName);
                group.Members.Add(pc, IdentityType.SamAccountName, userId);
                group.Save();
            }
            catch (Exception ex)
            {
                _logger.LogInformation(String.Format("{0}: Could not look add user {1} to group {2} on machine {3}. Error message: {4}", _slapsServiceName, userId, groupName, Environment.MachineName, ex.Message));

            }
        }

        private void DeleteAndRecreateUser(string userName, string password)
        {
            if (UserExists(userName))
            {

                try
                {
                    using var context = new PrincipalContext(ContextType.Machine);
                    using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, userName);
                    user.Delete();
          
                }
                catch (Exception ex)
                {
                    _logger.LogError(String.Format("{0}: Could not delete user {1} succesfully on machine {2}. Error message: {3}", _slapsServiceName, userName, Environment.MachineName, ex.Message));
                }

            }
            try
            {
                using var context = new PrincipalContext(ContextType.Machine);
                using var user = new UserPrincipal(context, userName, password, true);
                user.PasswordNeverExpires = true;
                user.Save();

                AddUserToGroup(user.SamAccountName, "Administrators");
                AddUserToGroup(user.SamAccountName, "Remote Desktop Users");
            }
            catch (Exception ex)
            {
                _logger.LogError(String.Format("{0}: Could not delete and  recreate user {1} on machine {2}. Error message: {3}", _slapsServiceName, userName, Environment.MachineName, ex.Message));
            }
        }

        private void CreateRegistryKeyIfAbsent(string path, string value, bool isSensitive, string defaultValue)
        {
            if (!RegistryValueExists(path, value)){
                WriteRegistryKey(path, value, isSensitive, defaultValue);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Cancellation token: " + stoppingToken.IsCancellationRequested);
            int interval = 24;
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Platform is windows: " + RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {

                    if (!RegistryKeyExists(_registeryPath))
                    {
                        CreateRegistryKey(_registeryPath, false);
                       
                    }
                    CreateRegistryKeyIfAbsent(_registeryPath, _timeStampKey, false, @"1587789392");
                    //Change this value to your own Azure Functions URL.
                    CreateRegistryKeyIfAbsent(_registeryPath, _azureFunctionsKeyWritePassword, true, @"[ !!! !! put here your Get function URL !! !!! ]");
                    //Change this to the username that you want for your local admin
                    CreateRegistryKeyIfAbsent(_registeryPath, _localAdminUserNameKey, false, @"SuperMaster");
                    //default interval at which this service will run.
                    CreateRegistryKeyIfAbsent(_registeryPath, _timeIntervalKey, false, @"24");

                    interval = Int32.Parse(GetRegistryKeyValue(_registeryPath, _timeIntervalKey));
                    _logger.LogInformation(String.Format("{0}: Service will run at {1} hours interval", _slapsServiceName, interval));

                    string azureFunctionUrl = GetRegistryKeyValue(_registeryPath, _azureFunctionsKeyWritePassword);
                    if (!HasRunToday(GetRegistryKeyValue(_registeryPath, _timeStampKey)))
                    {
                        _logger.LogInformation(String.Format("{0}: Refresh has not happend in the last {1} hours. Last refresh: {2}. Refreshing", _slapsServiceName, interval, ConvertEpochToDateTime(GetRegistryKeyValue(_registeryPath, _timeStampKey)).ToString()));
                        string body = jsonSerializer.Serialize(new AzureFunctionsBody
                        {
                            KeyName = Environment.MachineName,
                            ContentType = "Local Administrator Credentials",
                            Tags = new Dictionary<string, string>()
                            {
                                {"Username", GetRegistryKeyValue(_registeryPath, _localAdminUserNameKey)}
                            }
                        });


                        using HttpClient httpClient = new HttpClient();
                        StringContent bodyContent = new StringContent(body, Encoding.UTF8, "application/json"); ;
                       
                        using HttpResponseMessage response = await httpClient.PostAsync(azureFunctionUrl, bodyContent);
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogError(String.Format("{0}: Could not update password with Azure Key Function. Response code: {1}, Response Result: {2}. Content: {3}", _slapsServiceName, response.StatusCode, response.ReasonPhrase, response.Content.ToString()));
                        }
                        else
                        {
                            string apiResponse = await response.Content.ReadAsStringAsync();
                            string localAdminUser = GetRegistryKeyValue(_registeryPath, _localAdminUserNameKey);
                            DeleteAndRecreateUser(localAdminUser, apiResponse);
                            WriteRegistryKey(_registeryPath, _timeStampKey, false, DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString());
                        }
                        
                    }
                }
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(TimeSpan.FromHours(interval), stoppingToken);
            }
        }
    }
}
