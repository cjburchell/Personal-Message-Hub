using System.Text;
using System.Threading.Channels;
using MailKit.Net.Imap;
using MailKit.Security;
using MessageHub.Connectors;
using Xunit;

namespace MessageHub.Tests;

public sealed class YahooReaderTests
{
    // In-memory IMAP server: no socket, real account, TLS bypass in production, or provider traffic.
    private sealed class ImapScript : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly StringBuilder _pending = new();
        private byte[] _current = [];
        private int _offset;
        public List<string> Commands { get; } = [];
        public ImapScript() => Respond("* PREAUTH [CAPABILITY IMAP4rev1 NAMESPACE SPECIAL-USE UNSELECT] synthetic server\r\n");
        private void Respond(string response) => _responses.Writer.TryWrite(Encoding.UTF8.GetBytes(response));
        private void Command(string command)
        {
            Commands.Add(command);
            var tag = command.Split(' ')[0];
            var rest = command[(tag.Length + 1)..];
            if (rest == "CAPABILITY") Respond("* CAPABILITY IMAP4rev1 NAMESPACE SPECIAL-USE UNSELECT\r\n");
            else if (rest == "NAMESPACE") Respond("* NAMESPACE ((\"\" \"/\")) NIL NIL\r\n");
            else if (rest.StartsWith("LIST ", StringComparison.Ordinal))
                Respond("* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n* LIST (\\HasNoChildren \\Sent) \"/\" \"Sent\"\r\n");
            else if (rest.StartsWith("EXAMINE ", StringComparison.Ordinal))
                Respond("* FLAGS (\\Seen)\r\n* 2 EXISTS\r\n* OK [UIDVALIDITY 7] validity\r\n* OK [UIDNEXT 3] next\r\n");
            else if (rest.StartsWith("UID SEARCH ", StringComparison.Ordinal)) Respond("* SEARCH 1 2\r\n");
            else if (rest.StartsWith("UID FETCH ", StringComparison.Ordinal))
            {
                if (rest.Contains("BODY.PEEK[]", StringComparison.Ordinal))
                {
                    using var stream = new MemoryStream();
                    ConnectorTests.Mime().WriteTo(stream);
                    Respond("* 1 FETCH (UID 1 BODY[] {" + stream.Length + "}\r\n" + Encoding.UTF8.GetString(stream.ToArray()) + ")\r\n");
                }
                else Respond("* 1 FETCH (UID 1 INTERNALDATE \"10-Nov-2026 12:00:00 +0000\")\r\n");
            }
            else if (rest == "LOGOUT") Respond("* BYE closing\r\n");
            else if (rest != "UNSELECT") throw new InvalidOperationException("Unexpected synthetic IMAP command: " + rest);
            Respond(tag + " OK" + (rest.StartsWith("EXAMINE ", StringComparison.Ordinal) ? " [READ-ONLY]" : "") + " completed\r\n");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            _pending.Append(Encoding.UTF8.GetString(buffer, offset, count));
            while (_pending.ToString().IndexOf("\r\n", StringComparison.Ordinal) is var end && end >= 0)
            {
                var command = _pending.ToString(0, end);
                _pending.Remove(0, end + 2);
                Command(command);
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Write(buffer, offset, count); return Task.CompletedTask; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _current.Length) { _current = await _responses.Reader.ReadAsync(cancellationToken); _offset = 0; }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task YahooAdapterExaminesFoldersAndFetchesWithPeekWithoutMailboxMutations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var script = new ImapScript();
        using var client = new ImapClient();
        // Only this preauthenticated in-memory test uses None; production ConnectAsync forces SslOnConnect.
        await client.ConnectAsync(script, "imap.example.test", 993, SecureSocketOptions.None, timeout.Token);
        await using (var reader = new YahooReader(client))
        {
            var folders = await reader.GetFolderNamesAsync(timeout.Token);
            Assert.Equal(new[] { "INBOX", "Sent" }, folders);
            var state = await reader.OpenReadOnlyAsync("INBOX", timeout.Token);
            Assert.Equal(7u, state.UidValidity);
            Assert.Equal(2u, state.UpperUid);
            Assert.Equal(new uint[] { 1, 2 }, await reader.SearchAsync(0, DateTimeOffset.UtcNow.AddDays(-30), timeout.Token));
            var message = await reader.GetAsync(1, timeout.Token);
            Assert.NotNull(message);
            Assert.Equal("Synthetic subject", message.Mime.Subject);
            Assert.True((await reader.OpenReadOnlyAsync("Sent", timeout.Token)).IsSent);
        }
        Assert.Equal(2, script.Commands.Count(x => x.Contains(" EXAMINE ", StringComparison.Ordinal)));
        Assert.Contains(script.Commands, x => x.Contains("BODY.PEEK[]", StringComparison.Ordinal));
        Assert.DoesNotContain(script.Commands, x => x.Contains(" STORE ") || x.Contains(" EXPUNGE") || x.Contains(" SELECT ") || x.Contains(" APPEND "));
    }
}
