﻿using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using NTextCat;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using Microsoft.AspNet.SignalR.Client;
using SpeechHost.WebApi.Hub;
using wBeatSaberCamera.Models;
using wBeatSaberCamera.Twitch;
using wBeatSaberCamera.Utils;

namespace wBeatSaberCamera.Service
{
    public class SpeechHostSignalRClient : ObservableBase, ISpeechHostClient
    {
        private readonly int _port;
        private bool _isBusy;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (value == _isBusy)
                    return;

                _isBusy = value;
                OnPropertyChanged();
            }
        }

        private readonly TaskCompletionSource<object> _busyStartingProcess = new TaskCompletionSource<object>();
        private IHubProxy _hubProxy;
        private HubConnection _hubConnection;

        public SpeechHostSignalRClient(int port)
        {
            _port = port;
        }

        public async Task FillStreamWithSpeech(string ssml, Stream targetStream)
        {
            IsBusy = true;
            var sw = Stopwatch.StartNew();
            await _busyStartingProcess.Task;

            try
            {
                var response = await _hubProxy.Invoke<byte[]>("SpeakSsml", ssml);
                targetStream.Write(response, 0, response.Length);
            }
            finally
            {
                Console.WriteLine($"{DateTime.UtcNow.ToShortTimeString()}: Handling Speak took '{sw.Elapsed}'");
                IsBusy = false;
            }
        }

        public async Task<bool> Initialize()
        {
            var launchParams = new ProcessStartInfo()
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                FileName = "SpeechHost.WebApi.exe",
                Arguments = $"{_port}",
                CreateNoWindow = true
            };

            Process.Start(launchParams);

            _hubConnection = new HubConnection($"http://localhost:{_port}/signalr");
            _hubProxy = _hubConnection.CreateHubProxy("SpeechHub");
            await _hubConnection.Start();

            return await RetryPolicy.ExecuteAsync(async () =>
            {
                try
                {
                    var response = await _hubProxy.Invoke<string>("Hello");
                    if (response != "World")
                    {
                        Log.Error($"Expected the world, but only got '{response}'");
                        return false;
                    }

                    _busyStartingProcess.SetResult("finished");
                    return true;
                }
                catch (Exception e)
                {
                    throw new TransientException(e);
                }
            });
        }

        public void Dispose()
        {
            _hubConnection.Stop();
        }
    }

    public class SpeechHostClientCache
    {
        private int cacheIndex;

        public ObservableCollection<ISpeechHostClient> SpeechHostClients
        {
            get;
        } = new ObservableCollection<ISpeechHostClient>();

        public SpeechHostClientCache()
        {
            BindingOperations.EnableCollectionSynchronization(SpeechHostClients, new object());
        }

        private Task<ISpeechHostClient> clientCreator;

        private async Task<ISpeechHostClient> GetFreeClient(int tries = 3)
        {
            ISpeechHostClient client;

            try
            {
                client = await RetryPolicy.Execute(() =>
                {
                    int testCount = 0;
                    while (testCount++ < SpeechHostClients.Count)
                    {
                        if (cacheIndex > SpeechHostClients.Count - 1)
                        {
                            cacheIndex = 0;
                        }

                        client = SpeechHostClients[cacheIndex++ % SpeechHostClients.Count];

                        if (client.IsBusy)
                        {
                            continue;
                        }

                        return client;
                    }

                    throw new TransientException("All clients busy");
                }, tries);
                return client;
            }
            catch (Exception)
            {
                if (clientCreator == null || clientCreator.IsCompleted)
                {
                    clientCreator = Task.Run(async () =>
                    {
                        var newClient = new SpeechHostSignalRClient(FreeRandomTcpPort());
                        if (await newClient.Initialize())
                        {
                            SpeechHostClients.Add(newClient);
                            return (ISpeechHostClient)newClient;
                        }

                        newClient.Dispose();

                        throw new InvalidOperationException("couldnt create new client");
                    });
                }

                return await clientCreator;
            }

            throw new Exception("Could not get/create a new SpeechHostClient");
        }

        private int FreeRandomTcpPort()
        {
            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            int port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            tcpListener.Stop();
            return port;
        }

        public async Task FillStreamWithSpeech(string ssml, Stream targetStream)
        {
            await RetryPolicy.ExecuteAsync(async () =>
            {
                var client = await GetFreeClient();
                try
                {
                    await client.FillStreamWithSpeech(ssml, targetStream);
                }
                catch (Exception ex)
                {
                    SpeechHostClients.Remove(client);
                    client.Dispose();
                    throw new TransientException(ex);
                }
            });
        }
    }

    public class SpeechService
    {
        private readonly ChatViewModel _chatViewModel;
        private NaiveBayesLanguageIdentifier _lazyLanguagesIdentifier;
        private readonly AudioListener _audioListener;
        private readonly VrPositioningService _vrPositioningService;
        private readonly SpeechHostClientCache _speechHostClientCache = new SpeechHostClientCache();

        public SpeechService(ChatViewModel chatViewModel)
        {
            _chatViewModel = chatViewModel;
            _audioListener = new AudioListener();
            _vrPositioningService = new VrPositioningService();
        }

        public async Task Speak(Chatter chatter, string text, bool useLocalSpeak)
        {
            chatter.LastSpeakTime = DateTime.UtcNow;

            var language = GetLanguageFromText(text);
            try
            {
                using (var memoryStream = new MemoryStream())
                {
                    if (useLocalSpeak)
                    {
                        chatter.WriteSpeechToStream(language, text, memoryStream);
                    }
                    else
                    {
                        await _speechHostClientCache.FillStreamWithSpeech(chatter.GetSsmlFromText(language, text), memoryStream);
                    }

                    if (memoryStream.Length == 0)
                    {
                        return;
                    }

                    memoryStream.Position = 0;
                    var wavDuration = TimeSpan.FromMilliseconds(50);
                    try
                    {
                        wavDuration = new NAudio.Wave.WaveFileReader(memoryStream).TotalTime - TimeSpan.FromMilliseconds(800);
                        if (wavDuration < TimeSpan.FromMilliseconds(50))
                        {
                            wavDuration = TimeSpan.FromMilliseconds(50);
                        }
                    }
                    catch
                    {
                        // meh
                    }

                    memoryStream.Position = 0;
                    var soundEffect = SoundEffect.FromStream(memoryStream).CreateInstance();
                    var audioEmitter = new AudioEmitter();
                    if (_vrPositioningService.IsVrEnabled)
                    {
                        var hmdPositioning = _vrPositioningService.GetHmdPositioning();
                        audioEmitter.Position = Vector3.Transform(chatter.Position, -hmdPositioning.Rotation);
                    }

                    soundEffect.Apply3D(_audioListener, audioEmitter);
                    soundEffect.Play();

                    double sineTime = chatter.TrembleBegin;
                    var stopWatch = Stopwatch.StartNew();
                    while (stopWatch.Elapsed < wavDuration)
                    {
                        sineTime += chatter.TrembleSpeed;
                        await Task.Delay(10);

                        if (_vrPositioningService.IsVrEnabled)
                        {
                            var hmdPositioning = _vrPositioningService.GetHmdPositioning();

                            var newAudioEmitterPosition = Vector3.Transform(chatter.Position, hmdPositioning.Rotation);
                            audioEmitter.Velocity = (newAudioEmitterPosition - audioEmitter.Position) * 100;
                            audioEmitter.Position = newAudioEmitterPosition;

                            _audioListener.Velocity = hmdPositioning.Velocity - audioEmitter.Position + Vector3.Transform(audioEmitter.Position, new Quaternion(hmdPositioning.Omega, 1));
                            //_audioListener.Position = hmdPositioning.Position;
                            //Console.WriteLine(audioEmitter.Position + "/" + _audioListener.Position);
                            soundEffect.Apply3D(_audioListener, audioEmitter);

                            //am.Rotation = position.GetRotation();
                        }

                        var pitch = chatter.Pitch + Math.Sin(sineTime) * chatter.TrembleFactor;
                        if (pitch < -1)
                        {
                            pitch = -1;
                        }

                        if (pitch > 1)
                        {
                            pitch = 1;
                        }

                        pitch *= _chatViewModel.MaxPitchFactor;

                        //Console.WriteLine(pitch);
                        soundEffect.Pitch = (float)pitch;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error while text text '{text}': " + ex);
            }
        }

        private CultureInfo GetLanguageFromText(string text)
        {
            if (_lazyLanguagesIdentifier == null)
            {
                var factory = new NaiveBayesLanguageIdentifierFactory();

                _lazyLanguagesIdentifier = factory.Load(Assembly.GetExecutingAssembly().GetManifestResourceStream("wBeatSaberCamera.Ressource.LanguageProfile.xml"));
            }
            var res = _lazyLanguagesIdentifier.Identify(text);
            return GetCultureInfoFromIso3Name(res.FirstOrDefault()?.Item1.Iso639_3) ?? CultureInfo.InvariantCulture;
        }

        public static CultureInfo GetCultureInfoFromIso3Name(string name)
        {
            return CultureInfo
                   .GetCultures(CultureTypes.NeutralCultures)
                   .FirstOrDefault(c => c.ThreeLetterISOLanguageName == name);
        }
    }
}