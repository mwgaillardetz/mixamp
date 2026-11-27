using System.Net;

namespace CrateDeck.Core;

public record DeletionReview(string Server, string Library, Track Track);

public sealed class Deletion(Plex api, Scope scope, bool owner)
{
    public async Task<DeletionReview> Review(string id)
    {
        if (!owner)
            throw new IOException("The server owner must authorize file deletion.");
        var root = await api.Call("/");
        if (
            root.Str("machineIdentifier") != scope.Server
            || root.Str("allowMediaDeletion").ToLowerInvariant() is not ("true" or "1")
        )
            throw new IOException(
                "The server does not advertise media deletion for this connection."
            );
        var prefs = await api.Call("/:/prefs");
        var setting = prefs
            .Items("Setting")
            .FirstOrDefault(s => s.Str("id") == "allowMediaDeletion");
        if (setting != null && setting.Str("value").ToLowerInvariant() is not ("true" or "1"))
            throw new IOException("Allow media deletion is disabled.");
        var response = await api.Call("/library/metadata/" + Plex.E(id));
        var metadata = response.Items("Metadata").Single();
        var t = Track.Read(metadata);
        if (
            t.Id != id
            || metadata.Str("type") != "track"
            || (
                metadata.Str("librarySectionID") != scope.Library
                && response.Str("librarySectionID") != scope.Library
            )
            || !metadata.Items("Media").Any()
            || metadata.Items("Media").Any(m => !m.Items("Part").Any())
            || t.Parts.Any(p => p.File == "" || p.Id == "")
        )
            throw new IOException("Cannot establish this track’s complete deletion scope.");
        return new(scope.Server, scope.Library, t);
    }

    public async Task<bool> Commit(DeletionReview reviewed)
    {
        var fresh = await Review(reviewed.Track.Id);
        if (
            reviewed.Server != fresh.Server
            || reviewed.Library != fresh.Library
            || reviewed.Track.Identity != fresh.Track.Identity
            || !reviewed.Track.Parts.SequenceEqual(fresh.Track.Parts)
        )
            throw new IOException("The affected files changed. Review deletion again.");
        try
        {
            await api.Call("/library/metadata/" + Plex.E(fresh.Track.Id), HttpMethod.Delete);
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw;
        }
        catch (Exception)
        { /* The server may have committed before the acknowledgement was lost. */
        }
        try
        {
            await api.Fresh(fresh.Track.Id);
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception)
        {
            throw new IOException(
                "Deletion outcome unknown. Reconnect and sync before reviewing again."
            );
        }
    }
}
