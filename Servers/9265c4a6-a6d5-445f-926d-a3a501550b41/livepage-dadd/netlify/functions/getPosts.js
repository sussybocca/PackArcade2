import { createClient } from "@supabase/supabase-js";

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

export async function handler(event) {
  const slug = event.queryStringParameters.slug;

  const { data, error } = await supabase
    .from("posts")
    .select("*")
    .eq("page_slug", slug)
    .order("created_at", { ascending: false });

  if (error) {
    return { statusCode: 500, body: error.message };
  }

  return {
    statusCode: 200,
    body: JSON.stringify(data)
  };
}
