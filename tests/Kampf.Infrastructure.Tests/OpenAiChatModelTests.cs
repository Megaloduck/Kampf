using System.Net;
using System.Text;
using Kampf.Infrastructure.Ai;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public class OpenAiChatModelTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _contentType;

        public StubHandler(string body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var content = new StringContent(_body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    [Fact]
    public async Task A_streamed_answer_is_assembled_from_the_server_sent_events()
    {
        var handler = new StubHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Modu\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"larity\"}}]}\n\n" +
            "data: [DONE]\n\n",
            "text/event-stream");

        var model = new OpenAiCompatibleChatModel(new Uri("http://localhost:1234/v1/"), "local", handler);

        var answer = new StringBuilder();
        await foreach (var delta in model.StreamAsync("system", "question"))
            answer.Append(delta);

        Assert.Equal("Modularity", answer.ToString());
        Assert.Contains("\"stream\":true", handler.RequestBody);
    }

    [Fact]
    public async Task A_completion_reads_the_first_choice()
    {
        var handler = new StubHandler(
            "{\"choices\":[{\"message\":{\"content\":\"Modularity finds communities.\"}}]}",
            "application/json");

        var model = new OpenAiCompatibleChatModel(new Uri("http://localhost:1234/v1/"), "local", handler);

        var answer = await model.CompleteAsync("system", "question");

        Assert.Equal("Modularity finds communities.", answer);
    }
}
