namespace Diary.Update;

public sealed class UpdateChecker(IUpdateSource source)
{
    public async ValueTask<UpdateCheckResult> CheckAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await source.GetLatestAsync(
                request.ServerUri,
                request.Channel,
                request.Rid,
                request.Flavor,
                cancellationToken);
            if (envelope is null)
                return new UpdateCheckResult(UpdateCheckStatus.NoPublishedVersion);
            UpdateManifestValidator.Validate(envelope, request.Channel, request.Rid, request.Flavor);
            if (envelope.Manifest.MinUpdaterVersion > UpdateProtocol.UpdaterProtocolVersion)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UnsupportedUpdater,
                    envelope,
                    Error: $"该版本需要更新器协议 {envelope.Manifest.MinUpdaterVersion}，当前仅支持 {UpdateProtocol.UpdaterProtocolVersion}。");
            }
            if (envelope.Manifest.Sequence <= request.CurrentSequence)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, envelope);
            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                envelope,
                UpdateUris.FullPackage(request.ServerUri, envelope.Manifest));
        }
        catch (UpdateSourceException exception)
        {
            return new UpdateCheckResult(
                exception.Retryable ? UpdateCheckStatus.TemporarilyUnavailable : UpdateCheckStatus.InvalidResponse,
                Error: exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or UriFormatException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.InvalidResponse, Error: exception.Message);
        }
    }
}
