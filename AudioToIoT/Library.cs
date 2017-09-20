using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using System.Text;
using Microsoft.Azure.Devices.Client;
using System.IO;
using Microsoft.Azure.Devices.Client.Exceptions;

public class Library
{
    private const string audioFilename = "audio.mp3";
    private string filename;
    private MediaCapture capture;
    private InMemoryRandomAccessStream buffer;

    public static bool Recording;

    private async Task<bool> init()
    {
        if (buffer != null)
        {
            buffer.Dispose();
        }
        buffer = new InMemoryRandomAccessStream();
        if (capture != null)
        {
            capture.Dispose();
        }
        try
        {
            MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio
            };
            capture = new MediaCapture();
            await capture.InitializeAsync(settings);
            capture.RecordLimitationExceeded += (MediaCapture sender) =>
            {
                Stop();
                throw new Exception("Exceeded Record Limitation");
            };
            capture.Failed += (MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs) =>
            {
                Recording = false;
                throw new Exception(string.Format("Code: {0}. {1}", errorEventArgs.Code, errorEventArgs.Message));
            };
        }
        catch (Exception ex)
        {
            if (ex.InnerException != null && ex.InnerException.GetType() == typeof(UnauthorizedAccessException))
            {
                throw ex.InnerException;
            }
            throw;
        }
        return true;
    }

    public async void Record()
    {
        await init();
        await capture.StartRecordToStreamAsync(MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Auto), buffer);
        if (Recording) throw new InvalidOperationException("cannot excute two records at the same time");
        Recording = true;
    }

    public async void Stop()
    {
        await capture.StopRecordAsync();
        Recording = false;
    }

    public async Task Play(CoreDispatcher dispatcher)
    {
        MediaElement playback = new MediaElement();
        IRandomAccessStream audio = buffer.CloneStream();
        if (audio == null) throw new ArgumentNullException("buffer");
        StorageFolder storageFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
        if (!string.IsNullOrEmpty(filename))
        {
            StorageFile original = await storageFolder.GetFileAsync(filename);
            await original.DeleteAsync();
        }
        await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
        {
            StorageFile storageFile = await storageFolder.CreateFileAsync(audioFilename, CreationCollisionOption.GenerateUniqueName);
            filename = storageFile.Name;
            using (IRandomAccessStream fileStream = await storageFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                await RandomAccessStream.CopyAndCloseAsync(audio.GetInputStreamAt(0), fileStream.GetOutputStreamAt(0));
                await audio.FlushAsync();
                audio.Dispose();
            }
            IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.ReadWrite);
            SendDeviceToCloudMessagesAsync(stream); //Send to IoT Hub
            playback.SetSource(stream, storageFile.FileType);
            playback.Play();
            Debug.WriteLine(await ReceiveCloudToDeviceMessageAsync());

        });
    }

    static async void SendDeviceToCloudMessagesAsync(IRandomAccessStream song)
    {
        
        var deviceClient = DeviceClient.Create(iotHubUri,
                AuthenticationMethodFactory.
                    CreateAuthenticationWithRegistrySymmetricKey(deviceId, deviceKey),
                TransportType.Http1);


        var watch = System.Diagnostics.Stopwatch.StartNew();


        try
        {
            var ioStream = song.AsStream();
            ioStream.Seek(0, SeekOrigin.Begin);
            //song.AsStream().Seek(0, SeekOrigin.Begin);
            //Debug.WriteLine(song.AsStream());
            await deviceClient.UploadToBlobAsync("test_" + DateTime.Now.ToString().Replace(" ", "") +".mp3", ioStream);
        }
        catch (IotHubCommunicationException)
        {
            watch.Stop();
            Debug.WriteLine("Time to upload file timeout: {0}ms\n", watch.ElapsedMilliseconds);
        }

        Debug.WriteLine("Successful upload: {0}ms\n", watch.ElapsedMilliseconds);

        song.Dispose();

    }

    public static async Task<string> ReceiveCloudToDeviceMessageAsync()
    {

    var deviceClient = DeviceClient.CreateFromConnectionString("HostName=EGCSESyncWeek201758.azure-devices.net;DeviceId=guitardevice;SharedAccessKey=XZ2U7kCZgg0wphJJUwaw5drk9H2hU38kFPaPkJKcUtU=", TransportType.Amqp);

        while (true)
        {
            var receivedMessage = await deviceClient.ReceiveAsync();

            if (receivedMessage != null)
            {
                var messageData = Encoding.ASCII.GetString(receivedMessage.GetBytes());
                await deviceClient.CompleteAsync(receivedMessage);
                return messageData;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

}