import { createClient } from "@supabase/supabase-js";

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

export async function handler(event) {
  const { slug, content } = JSON.parse(event.body);

  if (!slug || !content) {
    return { statusCode: 400, body: "Missing data" };
  }

  const { error } = await supabase.from("posts").insert({
    page_slug: slug,
    content
  });

  if (error) {
    return { statusCode: 500, body: error.message };
  }

  return {
    statusCode: 200,
    body: "OK"
  };
}
