import io
import json
from pathlib import Path
import shutil
import subprocess
import unittest
from contextlib import redirect_stdout
from unittest.mock import AsyncMock, MagicMock, patch

from agent_framework import AgentResponse, ChatResponse, Message

import demo


class WorkflowTests(unittest.IsolatedAsyncioTestCase):
    @unittest.skipUnless(shutil.which("az"), "Azure CLI/Bicep is required for the infrastructure contract.")
    def test_project_deployment_waits_for_model(self) -> None:
        result = subprocess.run(
            ["az", "bicep", "build", "--file", "infra/main.bicep", "--stdout"],
            cwd=Path(__file__).parent, check=True, capture_output=True, text=True,
        )
        resources = json.loads(result.stdout)["resources"]
        project = next(item for item in resources
                       if item["type"] == "Microsoft.CognitiveServices/accounts/projects")
        self.assertTrue(any("Microsoft.CognitiveServices/accounts/deployments" in dependency
                            for dependency in project["dependsOn"]))

    async def test_real_workflow_preserves_stage_order_with_mocked_inference(self) -> None:
        async with (
            demo.AzureCliCredential() as credential,
            demo.AIProjectClient(
                endpoint="https://example.services.ai.azure.com/api/projects/demo",
                credential=credential,
            ) as project,
        ):
            client = demo.FoundryChatClient(project_client=project, model="model")
            with (
                patch.object(client, "_inner_get_response", new_callable=AsyncMock) as inference,
                patch("demo.FoundryChatClient", return_value=client),
                redirect_stdout(io.StringIO()) as text,
            ):
                inference.side_effect = [
                    ChatResponse(messages=[Message("assistant", ["Draft invitation"])]),
                    ChatResponse(messages=[Message("assistant", ["Edited invitation"])]),
                ]
                await demo.run_workflow("https://example/api/projects/demo", "model", "Invite people")
            self.assertEqual(inference.await_count, 2)
            self.assertLess(text.getvalue().index("Draft invitation"), text.getvalue().index("Edited invitation"))
            editor_messages = inference.call_args.kwargs["messages"]
            self.assertTrue(any("Draft invitation" in message.text for message in editor_messages))

    async def test_runs_writer_then_editor_and_prints_both_outputs(self) -> None:
        outputs = [
            AgentResponse(messages=[Message("assistant", ["Draft invitation"])]),
            AgentResponse(messages=[Message("assistant", ["Edited invitation"])]),
        ]
        client = MagicMock()
        writer, editor = MagicMock(), MagicMock()
        client.as_agent.side_effect = [writer, editor]
        workflow = MagicMock()
        workflow.run = AsyncMock(return_value=MagicMock(get_outputs=lambda: outputs))
        with (
            patch("demo.AzureCliCredential"),
            patch("demo.AIProjectClient"),
            patch("demo.FoundryChatClient", return_value=client),
            patch("demo.SequentialBuilder") as builder,
            redirect_stdout(io.StringIO()) as text,
        ):
            builder.return_value.build.return_value = workflow
            await demo.run_workflow("https://example/api/projects/demo", "model", "Invite people")
        builder.assert_called_once_with(participants=[writer, editor], output_from="all")
        workflow.run.assert_awaited_once_with("Invite people")
        self.assertIn("Draft invitation", text.getvalue())
        self.assertIn("Edited invitation", text.getvalue())
        self.assertEqual(client.as_agent.call_args_list[0].kwargs["name"], "writer")
        self.assertEqual(client.as_agent.call_args_list[1].kwargs["name"], "editor")

    async def test_missing_or_empty_stage_fails(self) -> None:
        for outputs in (
            [],
            [AgentResponse(), AgentResponse(messages=[Message("assistant", ["OK"])])],
        ):
            with self.subTest(outputs=outputs):
                workflow = MagicMock()
                workflow.run = AsyncMock(return_value=MagicMock(get_outputs=lambda: outputs))
                with (
                    patch("demo.AzureCliCredential"),
                    patch("demo.AIProjectClient"),
                    patch("demo.FoundryChatClient"),
                    patch("demo.SequentialBuilder") as builder,
                    redirect_stdout(io.StringIO()),
                ):
                    builder.return_value.build.return_value = workflow
                    with self.assertRaises(RuntimeError):
                        await demo.run_workflow("https://example", "model", "prompt")

    def test_invalid_configuration_fails_before_azure(self) -> None:
        for args in ([], ["--project-endpoint", "http://example"],
                     ["--project-endpoint", "https://example", "--prompt", " "]):
            with patch.dict("os.environ", {}, clear=True), redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit):
                    demo.main(args)


if __name__ == "__main__":
    unittest.main()
