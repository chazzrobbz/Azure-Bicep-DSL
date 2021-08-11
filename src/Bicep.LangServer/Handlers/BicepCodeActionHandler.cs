// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bicep.Core;
using Bicep.Core.CodeAction;
using Bicep.Core.Extensions;
using Bicep.LanguageServer.CompilationManager;
using Bicep.LanguageServer.Extensions;
using Bicep.LanguageServer.Utils;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Bicep.LanguageServer.Handlers
{
    public class BicepCodeActionHandler : CodeActionHandlerBase
    {
        private readonly ICompilationManager compilationManager;

        public BicepCodeActionHandler(ICompilationManager compilationManager)
        {
            this.compilationManager = compilationManager;
        }

        public override Task<CommandOrCodeActionContainer> Handle(CodeActionParams request, CancellationToken cancellationToken)
        {
            var compilationContext = this.compilationManager.GetCompilation(request.TextDocument.Uri);

            if (compilationContext == null)
            {
                return Task.FromResult(new CommandOrCodeActionContainer());
            }

            var requestStartOffset = PositionHelper.GetOffset(compilationContext.LineStarts, request.Range.Start);
            var requestEndOffset = request.Range.Start != request.Range.End
                ? PositionHelper.GetOffset(compilationContext.LineStarts, request.Range.End)
                : requestStartOffset;

            var semanticModel = compilationContext.Compilation.GetEntrypointSemanticModel();
            var diagnostics = semanticModel.GetAllDiagnostics();

            var quickFixes = diagnostics
                .Where(fixable =>
                    fixable.Span.ContainsInclusive(requestStartOffset) ||
                    fixable.Span.ContainsInclusive(requestEndOffset) ||
                    (requestStartOffset <= fixable.Span.Position && fixable.GetEndPosition() <= requestEndOffset))
                .OfType<IFixable>()
                .SelectMany(fixable => fixable.Fixes.Select(fix => CreateQuickFix(request.TextDocument.Uri, compilationContext, fix)));

            var disableLinterRules = diagnostics
                .Where(x =>
                    x.Span.ContainsInclusive(requestStartOffset) ||
                    x.Span.ContainsInclusive(requestEndOffset) ||
                    (requestStartOffset <= x.Span.Position && x.GetEndPosition() <= requestEndOffset))
                .OfType<IDisableLinterRule>()
                .Select(x => DisableLinterRule(request.TextDocument.Uri, x.AnalyzerCode));

            List<CommandOrCodeAction> commandOrCodeActions = new();

            commandOrCodeActions.AddRange(quickFixes);
            commandOrCodeActions.AddRange(disableLinterRules);

            return Task.FromResult(new CommandOrCodeActionContainer(commandOrCodeActions));
        }

        public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
        {
            // we are currently precomputing our quickfixes, so there's no need to resolve them after they are chosen
            // this shouldn't be called because registration options disabled the resolve functionality
            return Task.FromResult(request);
        }

        private static CommandOrCodeAction CreateQuickFix(DocumentUri uri, CompilationContext context, CodeFix fix)
        {
            return new CodeAction
            {
                Kind = CodeActionKind.QuickFix,
                Title = fix.Description,
                IsPreferred = fix.IsPreferred,
                Edit = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                    {
                        [uri] = fix.Replacements.Select(replacement => new TextEdit
                        {
                            Range = replacement.ToRange(context.LineStarts),
                            NewText = replacement.Text
                        })
                    }
                }
            };
        }

        private static CommandOrCodeAction DisableLinterRule(DocumentUri documentUri, string ruleName)
        {
            var directory = Path.GetDirectoryName(documentUri.GetFileSystemPath());

            if (directory is null)
            {
                throw new ArgumentException("Directory not found");
            }

            Command command =  Command.Create(LanguageConstants.DisableLinterRuleCommandName, ruleName, directory);

            return new CodeAction
            {
                Title = "DisableLinter",
                Command = command
            };
        }

        protected override CodeActionRegistrationOptions CreateRegistrationOptions(CodeActionCapability capability, ClientCapabilities clientCapabilities) => new()
        {
            DocumentSelector = DocumentSelectorFactory.Create(),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
            ResolveProvider = false
        };
    }
}
