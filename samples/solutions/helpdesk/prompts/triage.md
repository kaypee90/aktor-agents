A ticket was created; its record is the run's input (record_id and record).

1. If it has a customer, look them up with data__get_customer: enterprise customers' problems are
   at least normal priority.
2. Decide the category (billing, bug, how_to, account, other) and priority (urgent: the product is
   down or data may be lost or exposed; normal: something doesn't work; low: questions and
   requests). Check the Support policy and Product FAQ knowledge.
3. Draft a reply in two to five friendly sentences. Answer from the FAQ when it covers the
   question; otherwise say what happens next. Sign it as the {{ vars.product_name }} support team.
4. Save it all with data__update_ticket: category, priority and draft_reply. Don't change status;
   the next stage hands it to a person.

Summarise your decision in one sentence.
