﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;
using VSCompletion = Microsoft.VisualStudio.Language.Intellisense.Completion;
using Microsoft.CodeAnalysis.Snippets;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion.Presentation
{
#if NEWCOMPLETION
    internal sealed class CompletionSet3 : CompletionSet2
#else
    internal sealed class CompletionSet3 : CompletionSet
#endif
    {
        private readonly ForegroundThreadAffinitizedObject _foregroundObject = new ForegroundThreadAffinitizedObject();
        private readonly ITextView _textView;
        private readonly ITextBuffer _subjectBuffer;
        private readonly CompletionPresenterSession _completionPresenterSession;
        private Dictionary<PresentationItem, VSCompletion> _presentationItemMap;

        private CompletionHelper _completionHelper;
        private IReadOnlyList<IntellisenseFilter2> _filters;
        private IReadOnlyDictionary<CompletionItem, string> _completionItemToFilterText;

        public CompletionSet3(
            CompletionPresenterSession completionPresenterSession,
            ITextView textView,
            ITextBuffer subjectBuffer)
        {
            _completionPresenterSession = completionPresenterSession;
            _textView = textView;
            _subjectBuffer = subjectBuffer;
            this.Moniker = "All";
            this.DisplayName = "All";
        }

#if NEWCOMPLETION
        public override IReadOnlyList<IIntellisenseFilter> Filters => _filters;
#endif

        internal void SetTrackingSpan(ITrackingSpan trackingSpan)
        {
            this.ApplicableTo = trackingSpan;
        }

        internal void SetCompletionItems(
            IList<PresentationItem> completionItems,
            PresentationItem selectedItem,
            PresentationItem suggestionModeItem,
            bool suggestionMode,
            bool isSoftSelected,
            ImmutableArray<CompletionItemFilter> completionItemFilters,
            IReadOnlyDictionary<CompletionItem, string> completionItemToFilterText)
        {
            this._foregroundObject.AssertIsForeground();

            VSCompletion selectedCompletionItem = null;

            // Initialize the completion map to a reasonable default initial size (+1 for the builder)
            _presentationItemMap = _presentationItemMap ?? new Dictionary<PresentationItem, VSCompletion>(completionItems.Count + 1);
            _completionItemToFilterText = completionItemToFilterText;

            try
            {
                this.WritableCompletionBuilders.BeginBulkOperation();
                this.WritableCompletionBuilders.Clear();

                // If more than one filter was provided, then present it to the user.
                if (_filters == null && completionItemFilters.Length > 1)
                {
                    _filters = completionItemFilters.Select(f => new IntellisenseFilter2(this, f, GetLanguage()))
                                                    .ToArray();
                }

                var applicableToText = this.ApplicableTo.GetText(this.ApplicableTo.TextBuffer.CurrentSnapshot);

                var filteredSuggestionModeItem = new SimplePresentationItem(
                        CompletionItem.Create(
                            displayText: applicableToText,
                            span: this.ApplicableTo.GetSpan(this.ApplicableTo.TextBuffer.CurrentSnapshot).Span.ToTextSpan()),
                        selectedItem.CompletionService,
                        isSuggestionModeItem: true);

                var showBuilder = suggestionMode || suggestionModeItem != null;
                var bestSuggestionModeItem = applicableToText.Length > 0 ? filteredSuggestionModeItem : suggestionModeItem ?? filteredSuggestionModeItem;

                if (showBuilder && bestSuggestionModeItem != null)
                {
                    var suggestionModeCompletion = GetVSCompletion(bestSuggestionModeItem);
                    this.WritableCompletionBuilders.Add(suggestionModeCompletion);

                    if (selectedItem != null && selectedItem.IsSuggestionModeItem)
                    {
                        selectedCompletionItem = suggestionModeCompletion;
                    }
                }
            }
            finally
            {
                this.WritableCompletionBuilders.EndBulkOperation();
            }

            try
            {
                this.WritableCompletions.BeginBulkOperation();
                this.WritableCompletions.Clear();

                foreach (var item in completionItems)
                {
                    var completionItem = GetVSCompletion(item);
                    this.WritableCompletions.Add(completionItem);

                    if (item == selectedItem)
                    {
                        selectedCompletionItem = completionItem;
                    }
                }
            }
            finally
            {
                this.WritableCompletions.EndBulkOperation();
            }

            this.SelectionStatus = new CompletionSelectionStatus(
                selectedCompletionItem, isSelected: !isSoftSelected, isUnique: selectedCompletionItem != null);
        }

        private VSCompletion GetVSCompletion(PresentationItem item)
        {
            VSCompletion value;
            if (!_presentationItemMap.TryGetValue(item, out value))
            {
                value = new CustomCommitCompletion(
                    _completionPresenterSession,
                    item);
                _presentationItemMap.Add(item, value);
            }

            return value;
        }

        internal PresentationItem GetPresentationItem(VSCompletion completion)
        {
            // Linear search is ok since this is only called by the user manually selecting 
            // an item.  Creating a reverse mapping uses too much memory and affects GCs.
            foreach (var kvp in _presentationItemMap)
            {
                if (kvp.Value == completion)
                {
                    return kvp.Key;
                }
            }

            return null;
        }

        public override void SelectBestMatch()
        {
            // Do nothing.  We do *not* want the default behavior that the editor has.  We've
            // already computed the best match.
        }

        public override void Filter()
        {
            // Do nothing.  We do *not* want the default behavior that the editor has.  We've
            // already filtered the list.
        }

        public override void Recalculate()
        {
            // Do nothing.  Our controller will already recalculate if necessary.
        }

        private string GetLanguage()
        {
            var document = _subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document != null)
            {
                return document.Project.Language;
            }

            return "";
        }

        private CompletionHelper GetCompletionHelper()
        {
            _foregroundObject.AssertIsForeground();
            if (_completionHelper == null)
            {
                var document = _subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document != null)
                {
                    _completionHelper = CompletionHelper.GetHelper(document);
                }
            }

            return _completionHelper;
        }

#if NEWCOMPLETION
        public override IReadOnlyList<Span> GetHighlightedSpansInDisplayText(string displayText)
#else
        public IReadOnlyList<Span> GetHighlightedSpansInDisplayText(string displayText)
#endif
        {
            if (_completionItemToFilterText != null)
            {
                var completionHelper = this.GetCompletionHelper();
                if (completionHelper != null)
                {
                    var presentationItem = this._presentationItemMap.Keys.FirstOrDefault(k => k.Item.DisplayText == displayText);

                    if (presentationItem != null && !presentationItem.IsSuggestionModeItem)
                    {
                        string filterText;
                        if (_completionItemToFilterText.TryGetValue(presentationItem.Item, out filterText))
                        {
                            var highlightedSpans = completionHelper.GetHighlightedSpans(presentationItem.Item, filterText);
                            if (highlightedSpans != null)
                            {
                                return highlightedSpans.Select(s => s.ToSpan()).ToArray();
                            }
                        }
                    }
                }
            }

            return null;
        }

        internal void OnIntelliSenseFiltersChanged()
        {
            this._completionPresenterSession.OnIntelliSenseFiltersChanged(_filters);
        }
    }
}
