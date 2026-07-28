using AVFoundation;
using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace SafeVideoTransfer.Services;

public sealed class IosVideoRecordingService : IVideoRecordingService
{
	private static readonly object DelegateGate = new();
	private static readonly Dictionary<nint, CameraPickerDelegate> ActiveDelegates = [];

	public async Task<Models.VideoRecordingResult?> RecordAsync(
		string destinationPath, CancellationToken cancellationToken)
	{
		var camera = await Permissions.RequestAsync<Permissions.Camera>();
		var microphone = await Permissions.RequestAsync<Permissions.Microphone>();
		if (camera != PermissionStatus.Granted || microphone != PermissionStatus.Granted)
			throw new UnauthorizedAccessException("Camera and microphone permission are required.");
		if (!UIImagePickerController.IsSourceTypeAvailable(UIImagePickerControllerSourceType.Camera))
			throw new NotSupportedException("No camera is available. Use a physical iPhone for recording.");

		var started = DateTimeOffset.UtcNow;
		var temporaryPath = await PresentCameraAsync(cancellationToken);
		if (temporaryPath is null) return null;

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
			File.Move(temporaryPath, destinationPath, true);
			var asset = AVAsset.FromUrl(NSUrl.FromFilename(destinationPath));
			var seconds = asset.Duration.Seconds;
			return new Models.VideoRecordingResult(
				double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : DateTimeOffset.UtcNow - started);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
	}

	private static Task<string?> PresentCameraAsync(CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<string?>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		MainThread.BeginInvokeOnMainThread(() =>
		{
			var picker = new UIImagePickerController
			{
				SourceType = UIImagePickerControllerSourceType.Camera,
				MediaTypes = [UTTypes.Movie.Identifier],
				CameraCaptureMode = UIImagePickerControllerCameraCaptureMode.Video,
				CameraFlashMode = UIImagePickerControllerCameraFlashMode.Off,
				VideoQuality = UIImagePickerControllerQualityType.High
			};
			var pickerDelegate = new CameraPickerDelegate(completion);
			picker.Delegate = pickerDelegate;
			lock (DelegateGate) ActiveDelegates[picker.Handle] = pickerDelegate;
			picker.ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
			var presentingController = TopViewController();
			if (presentingController is null)
			{
				ReleaseDelegate(picker);
				completion.TrySetException(
					new InvalidOperationException("The camera screen could not be presented."));
				return;
			}
			presentingController.PresentViewController(picker, true, null);
		});
		cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
		return completion.Task;
	}

	private static UIViewController? TopViewController()
	{
		var controller = UIApplication.SharedApplication.ConnectedScenes
			.OfType<UIWindowScene>().SelectMany(x => x.Windows)
			.FirstOrDefault(x => x.IsKeyWindow)?.RootViewController;
		while (controller?.PresentedViewController is not null)
			controller = controller.PresentedViewController;
		return controller;
	}

	private sealed class CameraPickerDelegate(TaskCompletionSource<string?> completion)
		: UIImagePickerControllerDelegate
	{
		public override void FinishedPickingMedia(
			UIImagePickerController picker, NSDictionary info)
		{
			// Copy the native URL to a managed string before dismissing the picker.
			var temporaryPath = (info[UIImagePickerController.MediaURL] as NSUrl)?.Path;
			picker.DismissViewController(true, () =>
			{
				ReleaseDelegate(picker);
				if (string.IsNullOrWhiteSpace(temporaryPath))
					completion.TrySetException(
						new InvalidOperationException("iOS did not return the recorded video file."));
				else
					completion.TrySetResult(temporaryPath);
			});
		}

		public override void Canceled(UIImagePickerController picker) =>
			picker.DismissViewController(true, () =>
			{
				ReleaseDelegate(picker);
				completion.TrySetResult(null);
			});
	}

	private static void ReleaseDelegate(UIImagePickerController picker)
	{
		lock (DelegateGate) ActiveDelegates.Remove(picker.Handle);
	}
}
