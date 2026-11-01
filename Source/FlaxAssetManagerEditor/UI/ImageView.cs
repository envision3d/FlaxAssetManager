using System;
using System.IO;
using System.Threading.Tasks;
using FlaxEngine;
using FlaxEngine.GUI;

namespace FlaxAssetManager.UI
{
    /// <summary>
    /// Custom GUI control that renders a Texture2D using Render2D.DrawTexture.
    /// Loads images asynchronously from a byte array.
    /// </summary>
    public sealed class ImageView : Control, IDisposable
    {
        private Texture _texture;
        private bool _disposed;

        /// <summary>
        /// Current texture being rendered. Null means nothing to draw.
        /// </summary>
        public Texture Texture
        {
            get => _texture;
            set
            {
                if (_texture == value) return;
                FlaxEngine.Object.Destroy(ref _texture);
                _texture = value;
            }
        }

        /// <summary>
        /// Scale mode for the image within the control bounds.
        /// </summary>
        public ImageScaleMode ScaleMode { get; set; } = ImageScaleMode.AspectFit;

        public ImageView()
        {
            BackgroundColor = Color.Transparent;
        }

        /// <summary>
        /// Loads an image from a byte array. The bytes are written to a temp file and decoded by the
        /// engine's texture loader on the editor's main thread, then the temp file is removed.
        /// </summary>
        public void LoadFromBytes(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0 || _disposed)
                return;

            var tempPath = Path.Combine(Path.GetTempPath(), $"flaxassetmgr_preview_{Guid.NewGuid():N}.png");

            // Decode on the main thread, never on a thread-pool thread: Texture.FromFile creates a
            // GPU-side texture and Draw() consumes it. Flax installs no SynchronizationContext, so
            // the previous async/await flow resumed on a pool thread and created textures off-thread.
            _ = Scripting.RunOnUpdate(() =>
            {
                // The control can be disposed (pack reselected) while this is queued.
                if (_disposed)
                    return;
                try
                {
                    File.WriteAllBytes(tempPath, imageData);
                    Texture = Texture.FromFile(tempPath, generateMips: false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"FlaxAssetManager: Failed to load preview image: {ex.Message}");
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
            });
        }

        public void SetPlaceholder(Color placeholderColor)
        {
            // Draw a solid placeholder rect by setting a 1x1 texture with the color
            // We handle this in Draw() by checking if texture is null
        }

        /// <inheritdoc />
        public override void Draw()
        {
            base.Draw();

            if (_texture == null || _disposed)
                return;

            if (ScaleMode == ImageScaleMode.Stretch)
            {
                Render2D.DrawTexture(_texture, new Rectangle(Float2.Zero, Size), Color.White);
                return;
            }

            // AspectFit: scale to fit while preserving aspect ratio
            float scaleX = (float)Size.X / _texture.Width;
            float scaleY = (float)Size.Y / _texture.Height;
            float scale = Math.Min(scaleX, scaleY);

            float drawWidth = _texture.Width * scale;
            float drawHeight = _texture.Height * scale;

            float offsetX = (Size.X - drawWidth) * 0.5f;
            float offsetY = (Size.Y - drawHeight) * 0.5f;

            var rect = new Rectangle(
                new Float2(offsetX, offsetY),
                new Float2(drawWidth, drawHeight));

            Render2D.DrawTexture(_texture, rect, Color.White);
        }

        public void DisposeTexture()
        {
            if (_disposed) return;
            _disposed = true;
            FlaxEngine.Object.Destroy(ref _texture);
        }
    }

    /// <summary>
    /// How an image should be scaled within its container.
    /// </summary>
    public enum ImageScaleMode
    {
        /// <summary>Stretch image to fill the entire control area.</summary>
        Stretch,
        /// <summary>Scale to fit while preserving aspect ratio.</summary>
        AspectFit,
    }
}
