//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;


//using System.Drawing;
//using System.Collections;
//using System.ComponentModel;
//using System.Windows.Forms;
//using System.Data;

//using NationalInstruments.DAQmx;



namespace SDKTemplate
{
    // This scenario connects to the device selected in the "Discover
    // GATT Servers" scenario and communicates with it.
    // Note that this scenario is rather artificial because it communicates
    // with an unknown service with unknown characteristics.
    // In practice, your app will be interested in a specific service with
    // a specific characteristic.
    public sealed partial class Scenario2_Client : Page
    {
        private MainPage rootPage = MainPage.Current;

        private ObservableCollection<BluetoothLEAttributeDisplay> ServiceCollection = new ObservableCollection<BluetoothLEAttributeDisplay>();
        private ObservableCollection<BluetoothLEAttributeDisplay> CharacteristicCollection = new ObservableCollection<BluetoothLEAttributeDisplay>();

        private BluetoothLEDevice bluetoothLeDevice = null;
        private GattCharacteristic selectedCharacteristic;

        // Only one registered characteristic at a time.
        private GattCharacteristic registeredCharacteristic;
        private GattPresentationFormat presentationFormat;

        #region Error Codes
        readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE)
        #endregion

        #region UI Code
        public Scenario2_Client()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (string.IsNullOrEmpty(rootPage.SelectedBleDeviceId))
            {
                ConnectButton.IsEnabled = false;
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            var success = await ClearBluetoothLEDeviceAsync();
            if (!success)
            {
                rootPage.NotifyUser("Error: Unable to reset app state", NotifyType.ErrorMessage);
            }
        }
        #endregion

        #region Enumerating Services
        private async Task<bool> ClearBluetoothLEDeviceAsync()
        {
            if (subscribedForNotifications)
            {
                // Need to clear the CCCD from the remote device so we stop receiving notifications
                var result = await registeredCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                if (result != GattCommunicationStatus.Success)
                {
                    return false;
                }
                else
                {
                    selectedCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                    subscribedForNotifications = false;
                }
            }
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
            return true;
        }

        private async void ConnectButton_Click()
        {
            
            ConnectButton.IsEnabled = false;

            if (!await ClearBluetoothLEDeviceAsync())
            {
                rootPage.NotifyUser("Error: Unable to reset state, try again.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = false;
                return;
            }

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(rootPage.SelectedBleDeviceId);

                if (bluetoothLeDevice == null)
                {
                    rootPage.NotifyUser("Failed to connect to device.", NotifyType.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                rootPage.NotifyUser("Bluetooth radio is not on.", NotifyType.ErrorMessage);
            }

            if (bluetoothLeDevice != null)
            {
                // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
                // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
                // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    var services = result.Services;
                    rootPage.NotifyUser(String.Format("Found {0} services", services.Count), NotifyType.StatusMessage);
                    foreach (var service in services)
                    {
                        ServiceCollection.Add(new BluetoothLEAttributeDisplay(service));
                    }
                    ConnectButton.Visibility = Visibility.Collapsed;
                    ServiceList.Visibility = Visibility.Visible;
                }
                else
                {
                    rootPage.NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                }
            }
            ConnectButton.IsEnabled = true;
        }
        #endregion

        #region Enumerating Characteristics
        private async void ServiceList_SelectionChanged()
        {
            var attributeInfoDisp = (BluetoothLEAttributeDisplay)ServiceList.SelectedItem;

            CharacteristicCollection.Clear();
            RemoveValueChangedHandler();

            IReadOnlyList<GattCharacteristic> characteristics = null;
            try
            {
                // Ensure we have access to the device.
                var accessStatus = await attributeInfoDisp.service.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {
                    // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                    // and the new Async functions to get the characteristics of unpaired devices as well. 
                    var result = await attributeInfoDisp.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        characteristics = result.Characteristics;
                    }
                    else
                    {
                        rootPage.NotifyUser("Error accessing service.", NotifyType.ErrorMessage);

                        // On error, act as if there are no characteristics.
                        characteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    // Not granted access
                    rootPage.NotifyUser("Error accessing service.", NotifyType.ErrorMessage);

                    // On error, act as if there are no characteristics.
                    characteristics = new List<GattCharacteristic>();

                }
            }
            catch (Exception ex)
            {
                rootPage.NotifyUser("Restricted service. Can't read characteristics: " + ex.Message,
                    NotifyType.ErrorMessage);
                // On error, act as if there are no characteristics.
                characteristics = new List<GattCharacteristic>();
            }

            foreach (GattCharacteristic c in characteristics)
            {
                CharacteristicCollection.Add(new BluetoothLEAttributeDisplay(c));
            }
            CharacteristicList.Visibility = Visibility.Visible;
        }
        #endregion

        private void AddValueChangedHandler()
        {
            ValueChangedSubscribeToggle.Content = "Stop measurements and save RR intervals to a CSV file";
            if (!subscribedForNotifications)
            {
                registeredCharacteristic = selectedCharacteristic;
                registeredCharacteristic.ValueChanged += Characteristic_ValueChanged;
                subscribedForNotifications = true;
            }
        }

        private void RemoveValueChangedHandler()
        {
            ValueChangedSubscribeToggle.Content = "Subscribe to value changes";
            if (subscribedForNotifications)
            {
                registeredCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                registeredCharacteristic = null;
                subscribedForNotifications = false;
            }
        }

        private async void CharacteristicList_SelectionChanged()
        {
            selectedCharacteristic = null;

            var attributeInfoDisp = (BluetoothLEAttributeDisplay)CharacteristicList.SelectedItem;
            if (attributeInfoDisp == null)
            {
                EnableCharacteristicPanels(GattCharacteristicProperties.None);
                return;
            }

            selectedCharacteristic = attributeInfoDisp.characteristic;
            if (selectedCharacteristic == null)
            {
                rootPage.NotifyUser("No characteristic selected", NotifyType.ErrorMessage);
                return;
            }

            // Get all the child descriptors of a characteristics. Use the cache mode to specify uncached descriptors only 
            // and the new Async functions to get the descriptors of unpaired devices as well. 
            var result = await selectedCharacteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                rootPage.NotifyUser("Descriptor read failure: " + result.Status.ToString(), NotifyType.ErrorMessage);
            }

            // BT_Code: There's no need to access presentation format unless there's at least one. 
            presentationFormat = null;
            if (selectedCharacteristic.PresentationFormats.Count > 0)
            {

                if (selectedCharacteristic.PresentationFormats.Count.Equals(1))
                {
                    // Get the presentation format since there's only one way of presenting it
                    presentationFormat = selectedCharacteristic.PresentationFormats[0];
                }
                else
                {
                    // It's difficult to figure out how to split up a characteristic and encode its different parts properly.
                    // In this case, we'll just encode the whole thing to a string to make it easy to print out.
                }
            }

            // Enable/disable operations based on the GattCharacteristicProperties.
            EnableCharacteristicPanels(selectedCharacteristic.CharacteristicProperties);
        }

        private void SetVisibility(UIElement element, bool visible)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EnableCharacteristicPanels(GattCharacteristicProperties properties)
        {
            // BT_Code: Hide the controls which do not apply to this characteristic.
            SetVisibility(CharacteristicReadButton, properties.HasFlag(GattCharacteristicProperties.Read));

            SetVisibility(CharacteristicWritePanel,
                properties.HasFlag(GattCharacteristicProperties.Write) ||
                properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));
            CharacteristicWriteValue.Text = "";

            SetVisibility(ValueChangedSubscribeToggle, properties.HasFlag(GattCharacteristicProperties.Indicate) ||
                                                       properties.HasFlag(GattCharacteristicProperties.Notify));

        }

        private async void CharacteristicReadButton_Click()
        {
            // BT_Code: Read the actual value from the device by using Uncached.
            GattReadResult result = await selectedCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                string formattedResult = FormatValueByPresentation(result.Value, presentationFormat);
                rootPage.NotifyUser($"Read result: {formattedResult}", NotifyType.StatusMessage);
            }
            else
            {
                rootPage.NotifyUser($"Read failed: {result.Status}", NotifyType.ErrorMessage);
            }
        }

        private async void CharacteristicWriteButton_Click()
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var writeBuffer = CryptographicBuffer.ConvertStringToBinary(CharacteristicWriteValue.Text,
                    BinaryStringEncoding.Utf8);

                var writeSuccessful = await WriteBufferToSelectedCharacteristicAsync(writeBuffer);
            }
            else
            {
                rootPage.NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }

        private bool subscribedforcsvnotifications = false;
        private async void CharacteristicSaveButton_Click()
        {
            /*
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".csv");

            Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                // Application now has read/write access to the picked file
                // this.textBlock.Text = "Picked photo: " + file.Name;
                string strFilePath = file.Path;

                Csvwriter writer1 = new Csvwriter();



                //writer1.writecsv(strFilePath, stringrr);

                string strSeperator = ",";
                StringBuilder sbOutput = new StringBuilder();


                string[] dataarray = stringrr.ToArray();
                int datalength = dataarray.Length;

                string[] timearray = timelist.ToArray();

                string[] combinedarray = new string[datalength];
                for (int j = 0; j < datalength; j++)
                {
                    combinedarray[j] = dataarray[j] + " " + strSeperator + " " + timearray[j];
                }


                string[] nullarray = new string[1];
                nullarray[0] = "";

                await Windows.Storage.FileIO.WriteTextAsync(file, "RR intervals [ms]");
                await Windows.Storage.FileIO.AppendLinesAsync(file, nullarray);
                await Windows.Storage.FileIO.AppendLinesAsync(file, nullarray);



                await Windows.Storage.FileIO.AppendLinesAsync(file, combinedarray);

            }
            else
            {
                //this.textBlock.Text = "Operation cancelled.";
            }
            */
            FileSavePicker savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Plain Text", new List<string>() { ".csv" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = "RRintervals_1";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                // Prevent updates to the remote version of the file until we finish making changes and call CompleteUpdatesAsync.
                CachedFileManager.DeferUpdates(file);
                // write to file
                await FileIO.WriteTextAsync(file, file.Name);
                // Let Windows know that we're finished changing the file so the other app can update the remote version of the file.
                // Completing updates may require Windows to ask for user input.
                FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(file);
                if (status == FileUpdateStatus.Complete)
                {
                    //OutputTextBlock.Text = "File " + file.Name + " was saved.";


                    // Application now has read/write access to the picked file
                    // this.textBlock.Text = "Picked photo: " + file.Name;
                    string strFilePath = file.Path;

                    Csvwriter writer1 = new Csvwriter();



                    //writer1.writecsv(strFilePath, stringrr);

                    string strSeperator = ",";
                    StringBuilder sbOutput = new StringBuilder();


                    string[] dataarray = stringrr.ToArray();
                    int datalength = dataarray.Length;

                    string[] timearray = timelist.ToArray();

                    string[] combinedarray = new string[datalength];
                    for (int j = 0; j < datalength; j++)
                    {
                        combinedarray[j] = dataarray[j] + " " + strSeperator + " " + timearray[j];
                    }


                    string[] nullarray = new string[1];
                    nullarray[0] = "";

                    await Windows.Storage.FileIO.WriteTextAsync(file, "RR intervals [ms], time [hh:mm:ss]");
                    await Windows.Storage.FileIO.AppendLinesAsync(file, nullarray);
                    await Windows.Storage.FileIO.AppendLinesAsync(file, nullarray);


                     
                    await Windows.Storage.FileIO.AppendLinesAsync(file, combinedarray);

                    stringrr.Clear();
                    timelist.Clear();





                }
                else
                {
                    //OutputTextBlock.Text = "File " + file.Name + " couldn't be saved.";
                }
            }
            else
            {
                //OutputTextBlock.Text = "Operation cancelled.";
            }


        }

        private async void CharacteristicWriteButtonInt_Click()
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var isValidValue = Int32.TryParse(CharacteristicWriteValue.Text, out int readValue);
                if (isValidValue)
                {
                    var writer = new DataWriter();
                    writer.ByteOrder = ByteOrder.LittleEndian;
                    writer.WriteInt32(readValue);

                    var writeSuccessful = await WriteBufferToSelectedCharacteristicAsync(writer.DetachBuffer());
                }
                else
                {
                    rootPage.NotifyUser("Data to write has to be an int32", NotifyType.ErrorMessage);
                }
            }
            else
            {
                rootPage.NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }

        private async Task<bool> WriteBufferToSelectedCharacteristicAsync(IBuffer buffer)
        {
            try
            {
                // BT_Code: Writes the value from the buffer to the characteristic.
                var result = await selectedCharacteristic.WriteValueWithResultAsync(buffer);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    rootPage.NotifyUser("Successfully wrote value to device", NotifyType.StatusMessage);
                    return true;
                }
                else
                {
                    rootPage.NotifyUser($"Write failed: {result.Status}", NotifyType.ErrorMessage);
                    return false;
                }
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_INVALID_PDU)
            {
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED || ex.HResult == E_ACCESSDENIED)
            {
                // This usually happens when a device reports that it support writing, but it actually doesn't.
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
        }

        private bool subscribedForNotifications = false;
        private async void ValueChangedSubscribeToggle_Click()
        {
            if (!subscribedForNotifications)
            {
                // initialize status
                GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
                var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.None;
                if (selectedCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                {
                    cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                }

                else if (selectedCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;
                }

                try
                {
                    // BT_Code: Must write the CCCD in order for server to send indications.
                    // We receive them in the ValueChanged event handler.
                    status = await selectedCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                    if (status == GattCommunicationStatus.Success)
                    {
                        AddValueChangedHandler();
                        rootPage.NotifyUser("Measurements started", NotifyType.StatusMessage);
                    }
                    else
                    {
                        rootPage.NotifyUser($"Error registering for value changes: {status}", NotifyType.ErrorMessage);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    // This usually happens when a device reports that it support indicate, but it actually doesn't.
                    rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                }
            }
            else
            {
                try
                {
                    CharacteristicSaveButton_Click();
                    // BT_Code: Must write the CCCD in order for server to send notifications.
                    // We receive them in the ValueChanged event handler.
                    // Note that this sample configures either Indicate or Notify, but not both.
                    var result = await
                            selectedCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None);
                    if (result == GattCommunicationStatus.Success)
                    {
                        subscribedForNotifications = false;
                        RemoveValueChangedHandler();
                        rootPage.NotifyUser("Measurements ended", NotifyType.StatusMessage);
                    }
                    else
                    {
                        rootPage.NotifyUser($"Error un-registering for notifications: {result}", NotifyType.ErrorMessage);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    // This usually happens when a device reports that it support notify, but it actually doesn't.
                    rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                }
            }
        }

        private async void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            // BT_Code: An Indicate or Notify reported that the value has changed.
            // Display the new value with a timestamp.
            var newValue = FormatValueByPresentation(args.CharacteristicValue, presentationFormat);
            var message =  $"Values at {DateTime.Now:hh:mm:ss.FFF}: \r\n{newValue} beats/minute \r\nRR interval length: {stringrr[stringrr.Count-1]} ms";
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () => CharacteristicLatestValue.Text = message);
        }

        private string FormatValueByPresentation(IBuffer buffer, GattPresentationFormat format)
        {
            // BT_Code: For the purpose of this sample, this function converts only UInt32 and
            // UTF-8 buffers to readable text. It can be extended to support other formats if your app needs them.
            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);
            if (format != null)
            {
                if (format.FormatType == GattPresentationFormatTypes.UInt32 && data.Length >= 4)
                {
                    return BitConverter.ToInt32(data, 0).ToString();
                }
                else if (format.FormatType == GattPresentationFormatTypes.Utf8)
                {
                    try
                    {
                        return Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "(error: Invalid UTF-8 string)";
                    }
                }
                else
                {
                    // Add support for other format types as needed.
                    return "Unsupported format: " + CryptographicBuffer.EncodeToHexString(buffer);
                }
            }
            else if (data != null)
            {
                // We don't know what format to use. Let's try some well-known profiles, or default back to UTF-8.
                if (selectedCharacteristic.Uuid.Equals(GattCharacteristicUuids.HeartRateMeasurement))
                {
                    try
                    {
                        return "Heart Rate: " + ParseHeartRateValue(data).ToString();
                    }
                    catch (ArgumentException)
                    {
                        return "Heart Rate: (unable to parse)";
                    }
                }
                else if (selectedCharacteristic.Uuid.Equals(GattCharacteristicUuids.BatteryLevel))
                {
                    try
                    {
                        // battery level is encoded as a percentage value in the first byte according to
                        // https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.battery_level.xml
                        return "Battery Level: " + data[0].ToString() + "%";
                    }
                    catch (ArgumentException)
                    {
                        return "Battery Level: (unable to parse)";
                    }
                }
                // This is our custom calc service Result UUID. Format it like an Int
                else if (selectedCharacteristic.Uuid.Equals(Constants.ResultCharacteristicUuid))
                {
                    return BitConverter.ToInt32(data, 0).ToString();
                }
                // No guarantees on if a characteristic is registered for notifications.
                else if (registeredCharacteristic != null)
                {
                    // This is our custom calc service Result UUID. Format it like an Int
                    if (registeredCharacteristic.Uuid.Equals(Constants.ResultCharacteristicUuid))
                    {
                        return BitConverter.ToInt32(data, 0).ToString();
                    }
                }
                else
                {
                    try
                    {
                        return "Unknown format: " + Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "Unknown format";
                    }
                }
            }
            else
            {
                return "Empty data received";
            }
            return "Unknown format";
        }

        static List<string> stringrr = new List<string>();
        static List<string> timelist = new List<string>();



        /// <summary>
        /// Process the raw data received from the device into application usable data,
        /// according the the Bluetooth Heart Rate Profile.
        /// https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.heart_rate_measurement.xml&u=org.bluetooth.characteristic.heart_rate_measurement.xml
        /// This function throws an exception if the data cannot be parsed.
        /// </summary>
        /// <param name="data">Raw data received from the heart rate monitor.</param>
        /// <returns>The heart rate measurement value.</returns>
        private static ushort ParseHeartRateValue(byte[] data)
        {
            // Heart Rate profile defined flag values
            const byte heartRateValueFormat = 0x01;
            const byte rrflagbit = 0x10;
            const byte energyflagbit = 0x08;
            byte flags = data[0];


            bool isHeartRateValueSizeLong = ((flags & heartRateValueFormat) != 0);

            bool isrrpresent = ((flags & rrflagbit) != 0);
            bool isenergyflagbitpresent = ((flags & energyflagbit) != 0);

            int datalength = data.Length;
            int bytescombined = 0;

            if(isrrpresent)
            {
                if (isenergyflagbitpresent == false)
                {
                    if(isHeartRateValueSizeLong == false)
                    {
                        bytescombined = BitConverter.ToUInt16(data, 2);
                    }
                    else
                    {
                        bytescombined = BitConverter.ToUInt16(data, 3);
                    }
                }
                else
                {
                    if(isHeartRateValueSizeLong == false)
                    {
                        bytescombined = BitConverter.ToUInt16(data, 3);
                    }
                    else
                    {
                        bytescombined = BitConverter.ToUInt16(data, 4);
                    }
                }

               


                
            
                double bytescombined2 = Convert.ToDouble(bytescombined);
                double bytescombined3 = bytescombined2 / 1024;
                double bytescombined4 = bytescombined3 * 1000;



                //stringrr.Add(BitConverter.ToUInt16(data, 4).ToString());
                stringrr.Add(bytescombined4.ToString());
                timelist.Add($"{DateTime.Now:hh:mm:ss.FFF}");



                double longbytescombined;
                if (datalength >= 6)
                {
                    if (isenergyflagbitpresent == false)
                    {
                        if (isHeartRateValueSizeLong == false)
                        {
                            longbytescombined = BitConverter.ToUInt16(data, 4);
                        }
                        else
                        {
                            longbytescombined = BitConverter.ToUInt16(data, 5);
                        }
                    }
                    else
                    {
                        if (isHeartRateValueSizeLong == false)
                        {
                            longbytescombined = BitConverter.ToUInt16(data, 5);
                        }
                        else
                        {
                            longbytescombined = BitConverter.ToUInt16(data, 6);
                        }
                    }
                    double longbytescombined2 = Convert.ToDouble(longbytescombined);
                    double longbytescombined3 = longbytescombined2 / 1024;
                    double longbytescombined4 = longbytescombined3 * 1000;



                    //stringrr.Add(BitConverter.ToUInt16(data, 4).ToString());
                    stringrr.Add(longbytescombined4.ToString());
                    timelist.Add($"{DateTime.Now:hh:mm:ss.FFF}");

                }

                double values3bytescombined;
                if (datalength >= 8)
                {
                    if (isenergyflagbitpresent == false)
                    {
                        if (isHeartRateValueSizeLong == false)
                        {
                            values3bytescombined = BitConverter.ToUInt16(data, 6);
                        }
                        else
                        {
                            values3bytescombined = BitConverter.ToUInt16(data, 7);
                        }
                    }
                    else
                    {
                        if (isHeartRateValueSizeLong == false)
                        {
                            values3bytescombined = BitConverter.ToUInt16(data, 7);
                        }
                        else
                        {
                            values3bytescombined = BitConverter.ToUInt16(data, 8);
                        }
                    }
                    double values3bytescombined2 = Convert.ToDouble(values3bytescombined);
                    double values3bytescombined3 = values3bytescombined2 / 1024;
                    double values3bytescombined4 = values3bytescombined3 * 1000;



                    //stringrr.Add(BitConverter.ToUInt16(data, 4).ToString());
                    stringrr.Add(values3bytescombined4.ToString());
                    timelist.Add($"{DateTime.Now:hh:mm:ss.FFF}");

                }

                double values4bytescombined;
                if (datalength >= 10)
                {
                    if (isenergyflagbitpresent == false)
                    {
                        if (isHeartRateValueSizeLong == false)
                        {
                            values4bytescombined = BitConverter.ToUInt16(data, 8);
                        }
                        else
                        {
                            values4bytescombined = BitConverter.ToUInt16(data, 9);
                        }
                    }
                    else
                    {
                        if (isHeartRateValueSizeLong == false)
                        {
                            values4bytescombined = BitConverter.ToUInt16(data, 9);
                        }
                        else
                        {
                            values4bytescombined = BitConverter.ToUInt16(data, 10);
                        }
                    }
                    double values4bytescombined2 = Convert.ToDouble(values4bytescombined);
                    double values4bytescombined3 = values4bytescombined2 / 1024;
                    double values4bytescombined4 = values4bytescombined3 * 1000;



                    //stringrr.Add(BitConverter.ToUInt16(data, 4).ToString());
                    stringrr.Add(values4bytescombined4.ToString());
                    timelist.Add($"{DateTime.Now:hh:mm:ss.FFF}");

                }
            }
            

            if (isHeartRateValueSizeLong)
            {
                return BitConverter.ToUInt16(data, 1);
            }
            else
            {
                return data[1];
            }
        }

     
    }
    public class Csvwriter
    {
        public async void writecsv(string strFilePath, List<string> datalist)
        {

            //bool adf = File.Exists(strFilePath);
            string strSeperator = ",";
            StringBuilder sbOutput = new StringBuilder();
            /*
            int[][] inaOutput = new int[][]{
        new int[]{1000, 2000, 3000, 4000, 5000},
        new int[]{6000, 7000, 8000, 9000, 10000},
        new int[]{11000, 12000, 13000, 14000, 15000}
                };
            int ilength = inaOutput.GetLength(0);
            for (int i = 0; i < ilength; i++)
                sbOutput.AppendLine(string.Join(strSeperator, inaOutput[i]));*/

            string[] dataarray = datalist.ToArray();
            int ilength = dataarray.Length;
            for (int i = 0; i < ilength; i++)
            {
                sbOutput.AppendLine(string.Join(strSeperator, dataarray[i]));
            }
            //await Windows.Storage.FileIO.WriteTextAsync(file, string);



            // Create and write the csv file
            
            await Task.Run(() =>
            {
                Task.Yield();
                File.WriteAllText(strFilePath, sbOutput.ToString());
            });
            //File.WriteAllText(strFilePath, sbOutput.ToString());

            // To append more lines to the csv file
            //int illa = 0;
            //for (illa = 1; illa < 5; illa++)
            //    File.AppendAllText(strFilePath, sbOutput.ToString());
        }
    }
}
//string strFilePath = @"C:\Users\Leon\Desktop\testtest.csv"; ;
//string strSeperator = ",";
//StringBuilder sbOutput = new StringBuilder();

//if (3 < data.GetLength(0))
//{
//double abcd = BitConverter.ToDouble(data, 2);
//string vOut = BitConverter.ToString(data, 2, 1);
//string vOut = Convert.ToString(BitConverter.ToDouble(data, 2 /* Which byte position to convert */));
//sbOutput.AppendLine(string.Join(strSeperator, vOut));

// Create and write the csv file
//File.WriteAllText(strFilePath, sbOutput.ToString());

// To append more lines to the csv file
//File.AppendAllText(strFilePath, sbOutput.ToString());
//}