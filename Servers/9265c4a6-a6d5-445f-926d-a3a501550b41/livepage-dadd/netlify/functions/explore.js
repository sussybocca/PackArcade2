import { createClient } from "@supabase/supabase-js";

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

export async function handler(event) {
  try {
    // Optional: can accept query params like category, search, limit
    const { category } = event.queryStringParameters || {};

    // Fetch pages from Supabase
    let query = supabase
      .from("pages")
      .select("title, slug, category, content_type, usage_type, image_url, created_at")
      .order("created_at", { ascending: false })
      .limit(50);

    if (category) {
      query = query.eq("category", category);
    }

    const { data, error } = await query;

    if (error) {
      return {
        statusCode: 500,
        body: JSON.stringify({ error: "Failed to fetch pages", message: error.message })
      };
    }

    return {
      statusCode: 200,
      body: JSON.stringify({ pages: data })
    };
  } catch (err) {
    return {
      statusCode: 500,
      body: JSON.stringify({ error: "Function failed", message: err.message })
    };
  }
}
