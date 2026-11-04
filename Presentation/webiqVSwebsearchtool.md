# Microsoft Web IQ versus the Foundry web-search tool

## Naming matters in this repository

[Lab 3](../3-webiq-agent/README.md) retains the historical folder name
`3-webiq-agent`, but implements Foundry Agent Service's built-in web-search
tool. It does **not** provision or call the separate Microsoft Web IQ APIs.
The lab's comparison requires real search calls and structured citations;
an answer containing a URL is not sufficient evidence of retrieval.

| Question | Microsoft Web IQ APIs | Foundry Agent Service web-search tool |
| --- | --- | --- |
| What is integrated? | A separate suite of web search and grounding APIs. | A supported tool on a Foundry agent definition. |
| Who controls retrieval? | The calling application integrates the selected API and decides how to use returned evidence. | The agent service orchestrates tool use during response generation. |
| What is the teaching focus? | Building a custom retrieval/grounding integration. | Adding public-web grounding to a managed prompt agent and inspecting actual calls/citations. |
| What does this repository implement? | No dedicated Web IQ API lab. | Lab 3's with-tool/without-tool comparison. |

The tools are not interchangeable SDK class names. Their supported APIs,
models, terms, billing and availability must be checked independently.
The Foundry web-search documentation identifies its use of Grounding with
Bing Search / Bing Custom Search and the applicable terms.

Neither option is the same as [Foundry IQ](../11-foundry-iq/README.md) for a
configured knowledge corpus or [Work IQ](../35-work-iq/README.md) for
authorized Microsoft 365 context. Public-web grounding does not demonstrate
private enterprise permissions, and source citations do not guarantee that
every generated claim is correct.

## Suggested talk track

1. Show the same public question with and without the tool in lab 3.
2. Inspect completed search calls and structured URL annotations.
3. Explain that instructions alone cannot enable browsing.
4. Discuss data-sharing terms, source quality and extra usage charges before
   using either integration outside the synthetic workshop.

## Official references

- [Microsoft Web IQ FAQ](https://learn.microsoft.com/web-iq/resources/faq)
- [Microsoft Web IQ web-search API reference](https://learn.microsoft.com/web-iq/reference/web-search)
- [Foundry Agent Service web-search tool](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/web-search)
