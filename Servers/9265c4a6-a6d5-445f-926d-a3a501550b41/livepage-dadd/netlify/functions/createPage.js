import { createClient } from "@supabase/supabase-js";
import crypto from "crypto";
import fetch from "node-fetch";
import cookie from "cookie";

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

// Generate random secure page name
function generateRandomSlug() {
  return "l_" + crypto.randomBytes(4).toString("hex");
}

// Text moderation
async function moderateText(content, category) {
  const lowered = content.toLowerCase();
  const forbiddenFamily = ["nudity", "sex act"];
  const forbidden18Plus = ["illegal", "child", "nudity"];

  if (category === "family") {
    for (const word of forbiddenFamily) {
      if (lowered.includes(word)) throw new Error("Family-friendly content only.");
    }
  } else if (category === "18+") {
    for (const word of forbidden18Plus) {
      if (lowered.includes(word)) throw new Error("This content is not allowed.");
    }
  }
  return true;
}

// Image moderation
async function moderateImage(imageUrl, category) {
  if (!imageUrl) return true;
  const nudityWords = ["nudity", "naked"];
  const lowered = imageUrl.toLowerCase();
  for (const word of nudityWords) {
    if (lowered.includes(word)) throw new Error("This image has bad content.");
  }
  if (category === "family") throw new Error("Images not allowed in family pages.");
  return true;
}

// Verify age via Google OAuth code
async function verifyGoogleAge(oauthCode, redirectUri) {
  if (!oauthCode) return false;

  // Exchange code for access token
  const tokenResp = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      code: oauthCode,
      client_id: process.env.GOOGLE_CLIENT_ID,
      client_secret: process.env.GOOGLE_CLIENT_SECRET,
      redirect_uri: redirectUri,
      grant_type: "authorization_code"
    })
  });

  if (!tokenResp.ok) return false;

  const tokenData = await tokenResp.json();
  const accessToken = tokenData.access_token;
  if (!accessToken) return false;

  // Call People API to get birthday
  const resp = await fetch(
    "https://people.googleapis.com/v1/people/me?personFields=birthdays,emailAddresses",
    { headers: { Authorization: `Bearer ${accessToken}` } }
  );

  if (!resp.ok) return false;
  const data = await resp.json();
  if (!data.birthdays || !data.birthdays[0].date || !data.emailAddresses) return false;

  const birth = data.birthdays[0].date;
  const birthDate = new Date(birth.year, birth.month - 1, birth.day);
  const age = Math.floor((Date.now() - birthDate.getTime()) / (1000 * 60 * 60 * 24 * 365.25));

  // Save verified user in table if 18+
  if (age >= 18) {
    const email = data.emailAddresses[0].value;
    await supabase.from("VerifiedUsers").upsert({ email }).eq("email", email);
    return { verified: true, email };
  }

  return { verified: false };
}

export async function handler(event) {
  try {
    const { title, contentType, usageType, category, content, imageUrl, googleCode, redirectUri } = JSON.parse(event.body);

    if (!title || !contentType || !usageType || !category || !content) {
      return { statusCode: 400, body: JSON.stringify({ error: "Missing required fields" }) };
    }

    let ageVerified = false;
    let verifiedEmail = null;

    if (category === "18+") {
      const result = await verifyGoogleAge(googleCode, redirectUri);
      ageVerified = result.verified;
      verifiedEmail = result.email;

      if (!ageVerified) {
        return { statusCode: 403, body: JSON.stringify({ error: "You must be 18+ verified to post 18+ content" }) };
      }
    }

    // Moderate
    await moderateText(content, category);
    await moderateImage(imageUrl, category);

    const slug = generateRandomSlug();
    const { data, error } = await supabase
      .from("pages")
      .insert({
        title,
        slug,
        content_type: contentType,
        usage_type: usageType,
        content,
        category,
        age_verified: ageVerified,
        image_url: imageUrl || null
      })
      .select();

    if (error) {
      return { statusCode: 500, body: JSON.stringify({ error: "Supabase insert failed", message: error.message }) };
    }

    const headers = {};
    if (ageVerified && verifiedEmail) {
      headers["Set-Cookie"] = cookie.serialize("verifiedEmail", verifiedEmail, {
        httpOnly: true,
        secure: true,
        path: "/",
        maxAge: 60 * 60 * 24 * 30
      });
    }

    return { statusCode: 200, headers, body: JSON.stringify({ message: "Page created successfully", url: `/l/${slug}`, page: data[0] }) };
  } catch (err) {
    return { statusCode: 500, body: JSON.stringify({ error: "Function failed", message: err.message }) };
  }
}
