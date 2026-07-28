using Photos;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class IosPhotoLibraryService : IPhotoLibraryService
{
	public async Task SaveCopyAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		if (!record.LocalFileExists) throw new FileNotFoundException("Local video is missing.");
		var status = await PHPhotoLibrary.RequestAuthorizationAsync(PHAccessLevel.AddOnly);
		if (status is not PHAuthorizationStatus.Authorized and not PHAuthorizationStatus.Limited)
			throw new UnauthorizedAccessException("Photos add permission was not granted.");

		cancellationToken.ThrowIfCancellationRequested();
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
			() => PHAssetChangeRequest.FromVideo(
				Foundation.NSUrl.FromFilename(record.LocalPath)),
			(success, error) =>
			{
				if (success) completion.TrySetResult();
				else completion.TrySetException(new InvalidOperationException(
					error?.LocalizedDescription ?? "Photos did not save the video."));
			});
		await completion.Task;
	}
}
