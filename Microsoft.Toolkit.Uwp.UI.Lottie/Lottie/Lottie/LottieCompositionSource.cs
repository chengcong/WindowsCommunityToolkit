﻿#if DEBUG
// Uncomment this to slow down async awaits for testing.
//#define SlowAwaits
#endif
using LottieData;
using LottieData.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI.Xaml;

namespace Lottie
{
    /// <summary>
    /// A <see cref="CompositionSource"/> for a Lottie composition. This allows
    /// a Lottie to be specified as the source of a <see cref="Composition"/>.
    /// </summary>
    public sealed class LottieCompositionSource : ICompositionSource
    {
        readonly List<ICompositionSink> _sinks = new List<ICompositionSink>();
        readonly StorageFile _storageFile;
        int _loadVersion;
        Uri _uriSource;
        ContentFactory _contentFactory;

        /// <summary>
        /// Constructor to allow a <see cref="LottieCompositionSource"/> to be used in markup.
        /// </summary>
        public LottieCompositionSource() { }

        /// <summary>
        /// Creates a <see cref="CompositionSource"/> from a <see cref="StorageFile"/>.
        /// </summary>
        public LottieCompositionSource(StorageFile storageFile)
        {
            _storageFile = storageFile;
        }

        /// <summary>
        /// Gets or sets the Uniform Resource Identifier (URI) of the JSON source file for this <see cref="LottieCompositionSource"/>.
        /// </summary>
        public Uri UriSource
        {
            get => _uriSource;
            set
            {
                if (_uriSource == value)
                {
                    return;
                }
                _uriSource = value;
                StartLoading();
            }
        }

        public LottieCompositionOptions Options { get; set; }

        /// <summary>
        /// Called by XAML to convert a string to a <see cref="CompositionSource"/>.
        /// </summary>
        public static LottieCompositionSource CreateFromString(string uri)
        {
            var uriUri = StringToUri(uri);
            if (uriUri == null)
            {
                // TODO - throw?
                return null;
            }
            return new LottieCompositionSource { UriSource = uriUri };
        }

        // TODO: accept IRandomAccessStream
        [DefaultOverload]
        public IAsyncAction SetSourceAsync(StorageFile file)
        {
            _uriSource = null;
            return LoadAsync(new Loader(file)).AsAsyncAction();
        }

        public IAsyncAction SetSourceAsync(Uri sourceUri)
        {
            _uriSource = sourceUri;
            return LoadAsync(new Loader(sourceUri)).AsAsyncAction();
        }

        void ICompositionSource.ConnectSink(ICompositionSink sink)
        {
            Debug.Assert(!_sinks.Contains(sink));
            _sinks.Add(sink);
            if (_contentFactory != null)
            {
                _contentFactory.InstantiateContentForSink(sink);
            }
        }

        void ICompositionSource.DisconnectSink(ICompositionSink sink)
        {
            _sinks.Remove(sink);
        }

        // Starts a LoadAsync and returns immediately.
        async void StartLoading() => await LoadAsync(new Loader(UriSource));

        // Starts loading. Completes the returned task when the load completes or is replaced by another
        // load.
        async Task LoadAsync(Loader loader)
        {
            var loadVersion = ++_loadVersion;
            _contentFactory = null;

            // Notify all the sinks that their existing content is no longer valid.
            foreach (var sink in _sinks)
            {
                sink.SetContent(null, new System.Numerics.Vector2(), null, null, TimeSpan.Zero, null);
            }

            var contentFactory = await loader.Load(Options);
            if (loadVersion != _loadVersion)
            {
                // Another load request came in before this one completed.
                return;
            }

            // We are the the most recent load. Save the result.
            _contentFactory = contentFactory;

            // Instantiate content for each registered CompositionPlayer
            foreach (var sink in _sinks)
            {
                _contentFactory.InstantiateContentForSink(sink);
            }

            if (!contentFactory.CanInstantiate)
            {
                // The load failed.
                throw new ArgumentException("Failed to load composition.");
            }
        }

        // Handles loading a composition from a Lottie file.
        sealed class Loader
        {
            readonly Uri _uri;
            readonly StorageFile _storageFile;

            internal Loader(Uri uri) { _uri = uri; }

            internal Loader(StorageFile storageFile) { _storageFile = storageFile; }

            // Asynchronously loads WinCompData from a Lottie file.
            public async Task<ContentFactory> Load(LottieCompositionOptions Options)
            {
                var diagnostics = new LottieCompositionDiagnostics();
                diagnostics.Options = Options;

                var result = new ContentFactory(diagnostics);

                var sw = Stopwatch.StartNew();

                // Get the file name and contents.
                (var fileName, var jsonString) = await ReadFileAsync();
                diagnostics.FileName = fileName;
                diagnostics.ReadTime = sw.Elapsed;

                if (string.IsNullOrWhiteSpace(jsonString))
                {
                    // Failed to load ...
                    return result;
                }

                sw.Restart();

                // Parsing large Lottie files can take significant time. Do it on
                // another thread.
                LottieData.LottieComposition lottieComposition = null;
                await CheckedAwait(Task.Run(() =>
                {
                    lottieComposition =
                        LottieCompositionJsonReader.ReadLottieCompositionFromJsonString(
                            jsonString,
                            LottieCompositionJsonReader.Options.IgnoreMatchNames,
                            out var readerIssues);

                    diagnostics.JsonParsingIssues = readerIssues;
                }));

                diagnostics.ParseTime = sw.Elapsed;
                sw.Restart();

                if (lottieComposition == null)
                {
                    // Failed to load...
                    return result;
                }

                if (Options.HasFlag(LottieCompositionOptions.DiagnosticsIncludeXml) ||
                    Options.HasFlag(LottieCompositionOptions.DiagnosticsIncludeCSharpGeneratedCode))
                {
                    // Save the LottieComposition in the diagnostics so that the xml and codegen
                    // code can be derived from it.
                    diagnostics.LottieComposition = lottieComposition;
                }


                diagnostics.LottieVersion = lottieComposition.Version.ToString();
                diagnostics.LottieDetails = DescribeLottieComposition(lottieComposition);

                // For each marker, normalize to a progress value by subtracting the InPoint (so it is relative to the start of the animation)
                // and dividing by OutPoint - InPoint
                diagnostics.Markers = lottieComposition.Markers.Select(m =>
                {
                    // Normalize the marker InPoint value to a progress (0..1) value.
                    var markerProgress = (m.Frame - lottieComposition.InPoint) / (lottieComposition.OutPoint - lottieComposition.InPoint);
                    return new KeyValuePair<string, double>(m.Name, markerProgress);
                }).ToArray();

                result.SetDimensions(width: diagnostics.LottieWidth = lottieComposition.Width,
                                     height: diagnostics.LottieHeight = lottieComposition.Height,
                                     duration: diagnostics.Duration = lottieComposition.Duration);


                // Validate the composition and report if issues are found.
                diagnostics.LottieValidationIssues = LottieCompositionValidator.Validate(lottieComposition);

                diagnostics.ValidationTime = sw.Elapsed;
                sw.Restart();

                // Translating large Lotties can take significant time. Do it on another thread.
                bool translateSucceeded = false;
                WinCompData.Visual wincompDataRootVisual = null;
                await CheckedAwait(Task.Run(() =>
                {
                    translateSucceeded = LottieToVisualTranslator.TryTranslateLottieComposition(
                        lottieComposition,
                        false, // strictTranslation
                        true, // annotate
                        out wincompDataRootVisual,
                        out var translationIssues);

                    diagnostics.TranslationIssues = translationIssues;
                }));

                diagnostics.TranslationTime = sw.Elapsed;
                sw.Restart();

                if (!translateSucceeded)
                {
                    // Failed.
                    return result;
                }
                else
                {
                    if (Options.HasFlag(LottieCompositionOptions.DiagnosticsIncludeXml) ||
                        Options.HasFlag(LottieCompositionOptions.DiagnosticsIncludeCSharpGeneratedCode))
                    {
                        // Save the root visual so diagnostics can generate XML and codegen.
                        diagnostics.RootVisual = wincompDataRootVisual;
                    }
                    result.SetRootVisual(wincompDataRootVisual);
                    return result;
                }
            }

            Task<ValueTuple<string, string>> ReadFileAsync()
                    => _storageFile != null
                        ? ReadStorageFileAsync(_storageFile)
                        : ReadUriAsync(_uri);

            async Task<ValueTuple<string, string>> ReadUriAsync(Uri uri)
            {
                var absoluteUri = GetAbsoluteUri(uri);
                if (absoluteUri != null)
                {
                    if (absoluteUri.Scheme.StartsWith("ms-"))
                    {
                        return await ReadStorageFileAsync(await StorageFile.GetFileFromApplicationUriAsync(absoluteUri));
                    }
                    else
                    {
                        var winrtClient = new Windows.Web.Http.HttpClient();
                        var response = await winrtClient.GetAsync(absoluteUri);
                        var result = await response.Content.ReadAsStringAsync();
                        return ValueTuple.Create(absoluteUri.LocalPath, result);
                    }
                }
                return ValueTuple.Create<string, string>(null, null);
            }

            async Task<ValueTuple<string, string>> ReadStorageFileAsync(StorageFile storageFile)
            {
                Debug.Assert(storageFile != null);
                var result = await FileIO.ReadTextAsync(storageFile);
                return ValueTuple.Create(storageFile.Name, result);
            }
        }

        // Creates a string that describes the given LottieCompositionSource for diagnostics purposes.
        static string DescribeLottieComposition(LottieData.LottieComposition lottieComposition)
        {
            int precompLayerCount = 0;
            int solidLayerCount = 0;
            int imageLayerCount = 0;
            int nullLayerCount = 0;
            int shapeLayerCount = 0;
            int textLayerCount = 0;

            // Get the layers stored in assets.
            var layersInAssets =
                from asset in lottieComposition.Assets
                where asset.Type == Asset.AssetType.LayerCollection
                let layerCollection = (LayerCollectionAsset)asset
                from layer in layerCollection.Layers.GetLayersBottomToTop()
                select layer;

            foreach (var layer in lottieComposition.Layers.GetLayersBottomToTop().Concat(layersInAssets))
            {
                switch (layer.Type)
                {
                    case Layer.LayerType.PreComp:
                        precompLayerCount++;
                        break;
                    case Layer.LayerType.Solid:
                        solidLayerCount++;
                        break;
                    case Layer.LayerType.Image:
                        imageLayerCount++;
                        break;
                    case Layer.LayerType.Null:
                        nullLayerCount++;
                        break;
                    case Layer.LayerType.Shape:
                        shapeLayerCount++;
                        break;
                    case Layer.LayerType.Text:
                        textLayerCount++;
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }

            return $"LottieCompositionSource w={lottieComposition.Width} h={lottieComposition.Height} " +
                $"layers: precomp={precompLayerCount} solid={solidLayerCount} image={imageLayerCount} null={nullLayerCount} shape={shapeLayerCount} text={textLayerCount}";
        }

        // Parses a string into an absolute URI, or null if the string is malformed.
        static Uri StringToUri(string uri)
        {
            if (!Uri.IsWellFormedUriString(uri, UriKind.RelativeOrAbsolute))
            {
                return null;
            }

            return GetAbsoluteUri(new Uri(uri, UriKind.RelativeOrAbsolute));
        }

        // Returns an absolute URI. Relative URIs are made relative to ms-appx:///
        static Uri GetAbsoluteUri(Uri uri)
        {
            if (uri == null)
            {
                return null;
            }

            if (uri.IsAbsoluteUri)
            {
                return uri;
            }

            return new Uri($"ms-appx:///{uri}", UriKind.Absolute);
        }

        // Information from which a composition's content can be instantiated. Contains the WinCompData
        // translation of a composition and some metadata.
        sealed class ContentFactory
        {
            readonly LottieCompositionDiagnostics _diagnostics;
            WinCompData.Visual _wincompDataRootVisual;
            double _width;
            double _height;
            TimeSpan _duration;

            internal ContentFactory(LottieCompositionDiagnostics diagnostics)
            {
                _diagnostics = diagnostics;
            }

            internal void SetDimensions(double width, double height, TimeSpan duration)
            {
                _width = width;
                _height = height;
                _duration = duration;
            }

            internal void SetRootVisual(WinCompData.Visual rootVisual)
            {
                _wincompDataRootVisual = rootVisual;
            }

            internal bool CanInstantiate { get { return _wincompDataRootVisual != null; } }

            // Instantiates a new copy of the content and sets it on the given sink.
            // Requires _translatedLottie != null.
            internal void InstantiateContentForSink(ICompositionSink sink)
            {
                Windows.UI.Composition.Visual rootVisual = null;
                System.Numerics.Vector2 size = new System.Numerics.Vector2((float)_width, (float)_height);
                Windows.UI.Composition.CompositionPropertySet progressPropertySet = null;
                string progressPropertyName = LottieToVisualTranslator.ProgressPropertyName;
                TimeSpan duration = _duration;
                var diags = _diagnostics.Clone();
                object diagnostics = diags;

                if (_wincompDataRootVisual != null)
                {
                    var sw = Stopwatch.StartNew();

                    rootVisual = CompositionObjectFactory.CreateVisual(Window.Current.Compositor, _wincompDataRootVisual);
                    progressPropertySet = rootVisual.Properties;

                    diags.InstantiationTime = sw.Elapsed;
                }
                sink.SetContent(rootVisual, size, progressPropertySet, progressPropertyName, duration, diagnostics);
            }
        }

        #region DEBUG
        // For testing purposes, slows down a task.
#if SlowAwaits
        const int _checkedDelayMs = 5;
        async
#endif
        static Task CheckedAwait(Task task)
        {
#if SlowAwaits
            await Task.Delay(_checkedDelayMs);
            await task;
            await Task.Delay(_checkedDelayMs);
#else
            return task;
#endif
        }
        #endregion DEBUG
    }
}

