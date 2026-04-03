import json5
import json
import ollama
import os
import re
import shutil
import sqlite3
import torch
import whisper
from configs.init import start_emilia
from dotenv import load_dotenv
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from qwen_agent.utils.output_beautify import typewriter_print
from typing import List, Dict
from uuid import uuid4

load_dotenv()

app = FastAPI()

@app.on_event("startup")
async def startup_event():
    global agentic, parse_agentic_output, prompt_template
    agentic, parse_agentic_output, prompt_template = start_emilia()

STT_MODEL = os.getenv("STT_MODEL")
STT_LANGUAGE = os.getenv("STT_LANGUAGE")
WHISPER_MODEL = whisper.load_model(STT_MODEL)
CHAT_MODEL = os.getenv("CHAT_MODEL")
TOPIC_MODEL = os.getenv("TOPIC_MODEL")

def clear_cache(model = None):
    try:
        if model is not None:
            model.cpu()
            del model
            torch.cuda.empty_cache()
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error clearing model cache: {str(e)}")
    
def topic_summary(prompt: str, query: str):
    messages = [
        {"role": "system", "content": prompt},
        {"role": "user", "content": query}
    ]
    try:
        response = ollama.chat(
            model=TOPIC_MODEL,
            messages=messages
        )
        content = response['message']['content']
        return content
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error generating topic summary: {str(e)}")

def get_recent_chat(conv_id: str = None) -> str:
    conn = sqlite3.connect('emilia.db')
    cursor = conn.cursor()
    cursor.execute("""
        SELECT m.message, m.sent_at, u.name
        FROM messages m
        JOIN conversations c ON m.conversation_id = c.id
        JOIN users u ON c.user_id = u.id
        WHERE m.conversation_id = ?
        ORDER BY m.sent_at ASC
        LIMIT 10
    """, (conv_id,))
    rows = cursor.fetchall()
    conn.close()
    chats: List[Dict[str, str]] = [
        {"sent_at": sent_at, "message": message, "username": username}
        for message, sent_at, username in rows
    ]
    return json5.dumps(chats, ensure_ascii=False, separators=(',',':'))

def summary_chat(chat: List[Dict[str, str]]) -> str:
    if not chat:
        return "No chat messages found."
    
    messages = []
    for entry in chat:
        sent_at = entry['sent_at']
        message = entry['message']
        username = entry['username']
        messages.append(f"{sent_at} [{username}]: {message}")

    prompt_str = "Berikan ringkasan singkat dari percakapan antara pengguna sesuai username dengan chatbot bernama Emilia:\n\n" + "\n".join(messages)
    response = topic_summary(prompt_str, "")
    if response is None:
        raise HTTPException(status_code=500, detail="Failed to generate summary response.")
    return response

def transcribe_audio(audio_path):
    result = WHISPER_MODEL.transcribe(audio_path, language=STT_LANGUAGE)
    # clear_cache(model)
    return result['text']

def chat_response(prompt: str, query: str):
    messages = [
        {"role": "system", "content": prompt},
        {"role": "user", "content": query}
    ]
    try:
        response = ollama.chat(
            model=CHAT_MODEL,
            messages=messages,
            options={
                "temperature": 0.7,
                "top_P": 0.8,
                "top_k": 20,
                "repeat_penalty": 1
            }
        )
        raw_content = response['message']['content']
        cleaned = re.sub(r"<[^>]+>", "", raw_content).strip()
        return cleaned
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error generating response: {str(e)}")

def agentic_response(query: str, user_id: str, username: str = None) -> str:
    template_content = "User ID: {user_id}\nUsername: {username}\nQuery: {query}\n"
    content = template_content.format(user_id=user_id, username=username, query=query)
    messages = []
    messages.append({
        'role': 'user',
        'content': content
    })
    response = []
    response_plain_text = ''
    for response in agentic.run(messages=messages):
        response_plain_text = typewriter_print(response, response_plain_text)
    messages.extend(response)
    return response_plain_text

def journal_sentiment_response(journal_text: str) -> dict:
    prompt_str = """
    Kamu adalah asisten psikologi yang menilai fungsi emosional dari jurnal pribadi.

    Gunakan prinsip regulasi emosi simbolik:
    - Tulisan yang cenderung memicu pikiran berulang, rasa terprovokasi, atau memperpanjang emosi negatif tanpa pemahaman baru sebaiknya DIBUANG sebagai tindakan simbolik untuk memberi jarak emosional.
    - Tulisan yang bersifat reflektif, menenangkan, atau membantu memahami diri sebaiknya DISIMPAN.

    Tugasmu:
    1. Baca jurnal pengguna.
    2. Nilai fungsi utama tulisan:
    a) berpotensi memperpanjang tekanan emosional, atau
    b) membantu refleksi dan regulasi emosi.
    3. Tentukan apakah jurnal sebaiknya DISIMPAN atau DIBUANG.
    4. Berikan alasan singkat yang:
    - menjelaskan fungsi tulisan,
    - bersifat suportif,
    - membantu pengguna menerima keputusan tersebut tanpa membuka refleksi lanjutan.

    Aturan penting:
    - Jangan menghakimi.
    - Jangan memberi diagnosis.
    - Jangan memberi instruksi berbahaya.
    - Jangan menggunakan bahasa mengancam atau absolut.
    - Tulis reason dengan sudut pandang langsung kepada pengguna.
    - Gunakan kata ganti orang kedua seperti "kamu".
    - Hindari gaya laporan atau analisis pihak ketiga (contoh yang harus dihindari: "tulisan ini menunjukkan...", "jurnal ini mencerminkan...").
    - Fokus pada pengalaman dan perasaan pengguna, bukan pada teksnya.

    Jawab HANYA dengan JSON VALID, TANPA teks tambahan.

    Format output:
    {
    "decision": "simpan" | "buang",
    "reason": "2-3 kalimat singkat yang menjelaskan dampak tulisan ini terhadap proses berpikir dan perasaan, dengan nada persuasif yang menenangkan sesuai "decision"",
    "tone": "positif | negatif"
    }

    Note: Pastikan "decision" dan "tone" konsisten. Jika "decision" adalah "simpan", maka "tone" harus "positif", dan sebaliknya.
    """

    try:
        response = ollama.chat(
            model=CHAT_MODEL,
            messages=[
                {"role": "system", "content": prompt_str},
                {"role": "user", "content": journal_text}
            ],
            options={
                "temperature": 0.7,
                "top_P": 0.8,
                "top_k": 20,
                "repeat_penalty": 1
            }
        )

        raw = response.get("message", {}).get("content", "")

        if not raw or not raw.strip():
            raise ValueError("Model returned empty content")

        raw = raw.strip()

        # CASE 1️⃣: JSON langsung
        if raw.startswith("{"):
            try:
                data = json.loads(raw)
            except json.JSONDecodeError:
                data = None
        else:
            data = None

        # CASE 2️⃣: {"response": "<json string>"}
        if isinstance(data, dict) and "response" in data:
            inner = data["response"]

            if not inner or not inner.strip():
                raise ValueError("Inner JSON is empty")

            return json.loads(inner)

        # CASE 3️⃣: JSON langsung valid
        if isinstance(data, dict):
            return data

        # CASE 4️⃣: Ambil blok JSON dari teks (fallback terakhir)
        import re
        match = re.search(r"\{[\s\S]*\}", raw)
        if match:
            return json.loads(match.group())

        raise ValueError(f"Unparseable model output: {raw}")

    except Exception as e:
        raise HTTPException(
            status_code=500,
            detail=f"Error generating sentiment response: {str(e)}"
        )

# ================================================== ROUTES ==================================================

@app.post("/topic")
async def topic(
    user: str = Form(None),
    bot : str = Form(None)
):
    if user is None:
        raise HTTPException(status_code=400, detail="user must be provided.")

    question = f"pertanyaan : {user} \njawaban : {bot}"
    prompt_str = f"Berikan judul topik percakapan singkat dan tepat sesuai dengan pertanyaan, pastikan jawab dengan 5 kata atau kurang."

    response = topic_summary(prompt_str, question)

    if response is None:
        raise HTTPException(status_code=500, detail="Failed to generate topic response.")
    
    return {"response": response}

@app.post("/summary")
async def summary(
    conv_id: str = Form(None),
    ):
    if not conv_id:
        raise HTTPException(status_code=400, detail="Conversation ID must be provided.")
    try:
        summary_json = get_recent_chat(conv_id=conv_id)
        summary = json5.loads(summary_json)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error getting recent chat: {str(e)}")

    if not summary:
        raise HTTPException(status_code=404, detail="No chat found for the given conversation ID.")

    summary_text = summary_chat(summary)

    return {"response": summary_text}


@app.post("/transcribe")
async def transcribe(
    audio: UploadFile = File(None)
):
    if audio is None:
        raise HTTPException(status_code=400, detail="Audio file must be provided.")

    if audio.content_type not in ["audio/mpeg", "audio/wav", "audio/x-wav", "audio/mp3", "audio/x-mp3", "audio/m4a"]:
        raise HTTPException(status_code=400, detail="Invalid audio format. Only MP3 and WAV are supported.")

    folder_voice = "./voice_temp"
    if not os.path.exists(folder_voice):
        os.makedirs(folder_voice)

    audio_path = f"{folder_voice}/{uuid4()}.{audio.filename.split('.')[-1]}"
    with open(audio_path, "wb") as f:
        shutil.copyfileobj(audio.file, f)

    transcribed_text = transcribe_audio(audio_path)
    os.remove(audio_path)

    return {"response": transcribed_text}

@app.post("/chat")
async def chat(
    username: str = Form(None),
    question: str = Form(None),
):
    if question is None and audio is None:
        raise HTTPException(status_code=400, detail="Either 'question' or 'audio' must be provided.") 
    
    prompt_str = prompt_template.format(username=username)
    response = chat_response(prompt_str, question)
    
    if response is None:
        raise HTTPException(status_code=500, detail="Failed to generate chat response.")

    result = {"response": response}

    return result

@app.post("/sentiment")
async def sentiment(journal: str = Form(...)):
    result = journal_sentiment_response(journal)

    if result is None:
        raise HTTPException(
            status_code=500,
            detail="Failed to generate sentiment response."
        )

    return result

@app.post("/agentic")
async def agentic(
    user_id: str = Form(None),
    username: str = Form(None),
    question: str = Form(None),
    # audio: UploadFile = File(None)
):
    if user_id is None or question is None:
        raise HTTPException(status_code=400, detail="Both 'user_id' and 'question' must be provided.")
    
    if username is None:
        username = "Pengguna"
        
    # if audio:
    #     if audio.content_type not in ["audio/mpeg", "audio/wav", "audio/x-wav", "audio/mp3", "audio/x-mp3", "audio/m4a"]:
    #         raise HTTPException(status_code=400, detail="Invalid audio format. Only MP3 and WAV are supported.")
    #     folder_voice = "./voice_temp"
    #     if not os.path.exists(folder_voice):
    #         os.makedirs(folder_voice)
    #     audio_path = f"{folder_voice}/{uuid4()}.{audio.filename.split('.')[-1]}"
    #     with open(audio_path, "wb") as f:
    #         shutil.copyfileobj(audio.file, f)
    #     question = transcribe_audio(audio_path)
    #     os.remove(audio_path)

    try:
        response = agentic_response(query=question, user_id=user_id, username=username)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error processing request agentic: {str(e)}")
    
    if response is None:
        raise HTTPException(status_code=500, detail="Failed to generate agentic response.")
    response = parse_agentic_output(response)
    return {"result": response}

if __name__ == "__main__":
    import uvicorn
    agentic, parse_agentic_output, prompt_template = start_emilia()
    uvicorn.run(app, host="127.0.0.1", port=1204)